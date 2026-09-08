using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using BigFileDownloader.Models;
using BigFileDownloader.Services;
using Microsoft.Win32;

namespace BigFileDownloader;

public partial class MainWindow : Window
{
    private readonly DownloadEngine _engine = new();
    private readonly DownloadQueueStore _store = new();
    private readonly DispatcherTimer _saveTimer;
    private bool _queueDirty;
    private bool _isLoaded;
    private bool _isClosing;
    private bool _allowClose;
    private bool _saveInProgress;

    public ObservableCollection<DownloadJob> Jobs { get; } = [];

    public MainWindow()
    {
        InitializeComponent();
        DataContext = this;
        FolderBox.Text = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Downloads");

        _saveTimer = new DispatcherTimer(TimeSpan.FromSeconds(3), DispatcherPriority.Background, SaveTimer_Tick, Dispatcher);
        UpdateUiState();
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        if (_isLoaded)
        {
            return;
        }

        _isLoaded = true;
        var restoredJobs = await _store.LoadAsync();
        foreach (var job in restoredJobs.OrderByDescending(item => item.CreatedAt))
        {
            AttachJob(job);
            Jobs.Add(job);
        }

        DiagnosticLog.Info("UI", $"Main window loaded; restoredJobs={Jobs.Count}");
        UpdateUiState();
        UrlBox.Focus();
    }

    private async void AddButton_Click(object sender, RoutedEventArgs e) => await AddDownloadAsync();

    private async void UrlBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            await AddDownloadAsync();
        }
    }

    private async Task AddDownloadAsync()
    {
        var url = UrlBox.Text.Trim();
        var destination = FolderBox.Text.Trim();
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
        {
            MessageBox.Show("请输入有效的 HTTP 或 HTTPS 下载地址。", "无法添加", MessageBoxButton.OK, MessageBoxImage.Information);
            UrlBox.Focus();
            return;
        }

        if (string.IsNullOrWhiteSpace(destination))
        {
            MessageBox.Show("请选择保存文件夹。", "无法添加", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        try
        {
            Directory.CreateDirectory(destination);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(exception.Message, "无法使用保存位置", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        var job = new DownloadJob
        {
            Url = url,
            FileName = FileNameHelper.FromUrl(url),
            DestinationFolder = destination,
            SegmentCount = SelectedSegmentCount(),
            State = DownloadState.Pending,
            Message = "等待连接服务器"
        };
        AttachJob(job);
        Jobs.Insert(0, job);
        DiagnosticLog.Info(
            "UI",
            $"Task added; job={job.Id:N}; url={DiagnosticLog.SafeUrl(job.Url)}; " +
            $"segments={job.SegmentCount}; destination={job.DestinationFolder}");
        QueueGrid.SelectedItem = job;
        UrlBox.Clear();
        MarkDirty();
        UpdateUiState();
        await StartJobAsync(job);
    }

    private async Task StartJobAsync(DownloadJob job)
    {
        if (job.ActiveTask is { IsCompleted: false } || job.State == DownloadState.Completed)
        {
            return;
        }

        var cancellation = new CancellationTokenSource();
        DiagnosticLog.Info(
            "UI",
            $"Task starting; job={job.Id:N}; state={job.State}; downloadedBytes={job.DownloadedBytes}");
        job.Cancellation = cancellation;
        job.BytesPerSecond = 0;
        var progress = new Progress<DownloadProgress>(snapshot =>
        {
            job.ApplyProgress(snapshot);
            MarkDirty();
            UpdateUiState();
        });

        var task = RunJobCoreAsync(job, progress, cancellation);
        job.ActiveTask = task;
        UpdateUiState();
        await task;
    }

    private async Task RunJobCoreAsync(
        DownloadJob job,
        IProgress<DownloadProgress> progress,
        CancellationTokenSource cancellation)
    {
        try
        {
            await _engine.DownloadAsync(job, progress, cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            job.State = DownloadState.Completed;
            job.Message = "文件已保存";
            job.BytesPerSecond = 0;
            if (job.TotalBytes is > 0)
            {
                job.DownloadedBytes = job.TotalBytes.Value;
            }

            DiagnosticLog.Info(
                "UI",
                $"Task completed; job={job.Id:N}; file={job.ResolvedTargetPath}; bytes={job.DownloadedBytes}");
        }
        catch (OperationCanceledException)
        {
            job.State = DownloadState.Paused;
            job.Message = job.SupportsRanges
                ? "已保留分块，可继续下载"
                : "服务器不支持续传，继续时将从头下载";
            job.BytesPerSecond = 0;
            DiagnosticLog.Info(
                "UI",
                $"Task paused; job={job.Id:N}; resumable={job.SupportsRanges}; downloadedBytes={job.DownloadedBytes}");
        }
        catch (Exception exception)
        {
            job.State = DownloadState.Failed;
            job.Message = FriendlyMessage(exception);
            job.BytesPerSecond = 0;
            DiagnosticLog.Error("UI", $"Task failed; job={job.Id:N}", exception);
        }
        finally
        {
            cancellation.Dispose();
            job.Cancellation = null;
            job.ActiveTask = null;
            MarkDirty();
            UpdateUiState();
            await SaveQueueAsync();
        }
    }

    private void PauseButton_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedJob() is not { } job || job.Cancellation is null)
        {
            return;
        }

        job.State = DownloadState.Pausing;
        job.Message = "正在安全停止连接";
        DiagnosticLog.Info("UI", $"Pause requested; job={job.Id:N}");
        job.Cancellation.Cancel();
        UpdateUiState();
    }

    private async void ResumeButton_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedJob() is { State: DownloadState.Paused or DownloadState.Pending } job)
        {
            DiagnosticLog.Info("UI", $"Resume requested; job={job.Id:N}");
            await StartJobAsync(job);
        }
    }

    private async void RetryButton_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedJob() is { State: DownloadState.Failed } job)
        {
            DiagnosticLog.Info("UI", $"Retry requested; job={job.Id:N}");
            job.Message = "正在重试";
            await StartJobAsync(job);
        }
    }

    private async void UpdateLinkButton_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedJob() is not { State: DownloadState.Pending or DownloadState.Paused or DownloadState.Failed } job)
        {
            return;
        }

        var url = UrlBox.Text.Trim();
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
        {
            MessageBox.Show(
                "请先在下载地址输入框粘贴新的 HTTP 或 HTTPS 地址。",
                "无法更新链接",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            UrlBox.Focus();
            return;
        }

        DiagnosticLog.Info(
            "UI",
            $"Task URL updated; job={job.Id:N}; old={DiagnosticLog.SafeUrl(job.Url)}; new={DiagnosticLog.SafeUrl(url)}");
        job.Url = url;
        job.State = DownloadState.Pending;
        job.Message = "链接已更新，正在验证并继续";
        UrlBox.Clear();
        MarkDirty();
        UpdateUiState();
        await StartJobAsync(job);
    }

    private void BrowseButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "选择下载保存位置",
            InitialDirectory = Directory.Exists(FolderBox.Text) ? FolderBox.Text : null
        };

        if (dialog.ShowDialog(this) == true)
        {
            FolderBox.Text = dialog.FolderName;
        }
    }

    private void OpenButton_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedJob() is { } job)
        {
            OpenJobLocation(job);
        }
    }

    private void LogButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            DiagnosticLog.Info("UI", "Opening current diagnostic log");
            Process.Start(new ProcessStartInfo(DiagnosticLog.CurrentLogPath) { UseShellExecute = true });
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or Win32Exception)
        {
            DiagnosticLog.Error("UI", "Unable to open diagnostic log", exception);
            MessageBox.Show(exception.Message, "无法打开诊断日志", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void RemoveButton_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedJob() is not { } job)
        {
            return;
        }

        var result = MessageBox.Show(
            "移除任务时是否同时删除未完成的临时分块？\n\n是：删除任务和临时分块\n否：仅从列表移除",
            "移除下载任务",
            MessageBoxButton.YesNoCancel,
            MessageBoxImage.Question);
        if (result == MessageBoxResult.Cancel)
        {
            return;
        }

        job.Cancellation?.Cancel();
        if (job.ActiveTask is not null)
        {
            await job.ActiveTask;
        }

        if (result == MessageBoxResult.Yes)
        {
            try
            {
                _engine.DeletePartialData(job);
            }
            catch (IOException exception)
            {
                MessageBox.Show(exception.Message, "临时文件未完全删除", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        DetachJob(job);
        Jobs.Remove(job);
        DiagnosticLog.Info("UI", $"Task removed; job={job.Id:N}; partialDataDeleted={result == MessageBoxResult.Yes}");
        MarkDirty();
        UpdateUiState();
        await SaveQueueAsync();
    }

    private async void ClearCompletedButton_Click(object sender, RoutedEventArgs e)
    {
        var completed = Jobs.Where(job => job.State == DownloadState.Completed).ToList();
        if (completed.Count == 0)
        {
            return;
        }

        if (MessageBox.Show(
                $"从列表移除 {completed.Count} 个已完成任务？\n已下载文件不会删除。",
                "清理已完成任务",
                MessageBoxButton.OKCancel,
                MessageBoxImage.Question) != MessageBoxResult.OK)
        {
            return;
        }

        foreach (var job in completed)
        {
            DetachJob(job);
            Jobs.Remove(job);
        }

        DiagnosticLog.Info("UI", $"Completed tasks cleared; count={completed.Count}");
        MarkDirty();
        UpdateUiState();
        await SaveQueueAsync();
    }

    private void QueueGrid_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateUiState();

    private void QueueGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (SelectedJob() is { } job)
        {
            OpenJobLocation(job);
        }
    }

    private void OpenJobLocation(DownloadJob job)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(job.ResolvedTargetPath) && File.Exists(job.ResolvedTargetPath))
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{job.ResolvedTargetPath}\"") { UseShellExecute = true });
            }
            else
            {
                Directory.CreateDirectory(job.DestinationFolder);
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{job.DestinationFolder}\"") { UseShellExecute = true });
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or Win32Exception)
        {
            DiagnosticLog.Error("UI", $"Unable to open task location; job={job.Id:N}", exception);
            MessageBox.Show(exception.Message, "无法打开文件夹", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_allowClose)
        {
            _saveTimer.Stop();
            _engine.Dispose();
            return;
        }

        e.Cancel = true;
        if (_isClosing)
        {
            return;
        }

        _isClosing = true;
        DiagnosticLog.Info("UI", $"Shutdown requested; activeJobs={Jobs.Count(item => item.ActiveTask is { IsCompleted: false })}");
        _saveTimer.Stop();
        IsEnabled = false;
        var activeTasks = Jobs
            .Where(item => item.ActiveTask is { IsCompleted: false })
            .Select(item => item.ActiveTask!)
            .ToArray();
        foreach (var job in Jobs.Where(item => item.ActiveTask is { IsCompleted: false }))
        {
            job.Cancellation?.Cancel();
        }

        try
        {
            await Task.WhenAll(activeTasks);
            _queueDirty = true;
            await SaveQueueAsync();
        }
        catch
        {
            DiagnosticLog.Warning("UI", "Queue could not be fully saved during shutdown");
            // The queue is best-effort during process shutdown.
        }

        _allowClose = true;
        Close();
    }

    private void SaveTimer_Tick(object? sender, EventArgs e)
    {
        if (_queueDirty)
        {
            _ = SaveQueueAsync();
        }
    }

    private async Task SaveQueueAsync()
    {
        if (!_queueDirty || _saveInProgress)
        {
            return;
        }

        var snapshot = Jobs.ToList();
        _queueDirty = false;
        _saveInProgress = true;
        try
        {
            await _store.SaveAsync(snapshot);
        }
        catch (IOException exception)
        {
            _queueDirty = true;
            DiagnosticLog.Error("Queue", "Unable to save task queue", exception);
            StorageText.Text = $"任务列表保存失败：{exception.Message}";
        }
        finally
        {
            _saveInProgress = false;
        }
    }

    private void AttachJob(DownloadJob job)
    {
        job.PropertyChanged += Job_PropertyChanged;
    }

    private void DetachJob(DownloadJob job)
    {
        job.PropertyChanged -= Job_PropertyChanged;
    }

    private void Job_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        MarkDirty();
        UpdateUiState();
    }

    private void MarkDirty()
    {
        _queueDirty = true;
        if (!_saveTimer.IsEnabled)
        {
            _saveTimer.Start();
        }
    }

    private DownloadJob? SelectedJob() => QueueGrid.SelectedItem as DownloadJob;

    private int SelectedSegmentCount()
    {
        return SegmentBox.SelectedItem is ComboBoxItem { Tag: string value }
            && int.TryParse(value, out var result)
                ? result
                : 4;
    }

    private void UpdateUiState()
    {
        var selected = SelectedJob();
        PauseButton.IsEnabled = selected?.State is DownloadState.Inspecting or DownloadState.Downloading or DownloadState.Merging;
        ResumeButton.IsEnabled = selected?.State is DownloadState.Pending or DownloadState.Paused;
        RetryButton.IsEnabled = selected?.State == DownloadState.Failed;
        UpdateLinkButton.IsEnabled = selected?.State is DownloadState.Pending or DownloadState.Paused or DownloadState.Failed;
        OpenButton.IsEnabled = selected is not null;
        RemoveButton.IsEnabled = selected is not null;
        ClearCompletedButton.IsEnabled = Jobs.Any(job => job.State == DownloadState.Completed);
        EmptyState.Visibility = Jobs.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        var active = Jobs.Count(job => job.State is DownloadState.Inspecting or DownloadState.Downloading or DownloadState.Merging);
        var completed = Jobs.Count(job => job.State == DownloadState.Completed);
        var speed = Jobs.Sum(job => job.BytesPerSecond);
        SummaryText.Text = $"任务 {Jobs.Count}  ·  正在下载 {active}  ·  已完成 {completed}  ·  总速度 {(speed > 0 ? DownloadJob.FormatBytes((long)speed) + "/s" : "--")}";

        try
        {
            var root = Path.GetPathRoot(FolderBox.Text);
            if (!string.IsNullOrWhiteSpace(root))
            {
                var drive = new DriveInfo(root);
                StorageText.Text = $"{drive.Name} 可用 {DownloadJob.FormatBytes(drive.AvailableFreeSpace)}";
            }
        }
        catch
        {
            StorageText.Text = string.Empty;
        }
    }

    private static string FriendlyMessage(Exception exception)
    {
        return exception switch
        {
            UnauthorizedAccessException => "没有写入目标文件夹的权限",
            HttpRequestException { StatusCode: HttpStatusCode.Unauthorized } =>
                "服务器要求登录；当前版本不能读取浏览器的登录 Cookie",
            HttpRequestException { StatusCode: HttpStatusCode.Forbidden } =>
                "服务器拒绝请求（403）。链接可能已过期或受登录、来源页、并发限制；获取新链接后可更新任务并续传",
            HttpRequestException { StatusCode: HttpStatusCode.NotFound } => "服务器上没有找到该文件",
            HttpRequestException { StatusCode: HttpStatusCode.TooManyRequests } =>
                "服务器限制请求频率；请降低并发连接数，稍后重试",
            HttpRequestException requestException => requestException.Message,
            IOException ioException => ioException.Message,
            _ => exception.Message
        };
    }
}
