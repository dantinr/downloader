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
using BigFileDownloader.Views;
using Microsoft.Win32;

namespace BigFileDownloader;

public partial class MainWindow : Window
{
    private enum RemovalMode
    {
        RecordOnly,
        RecordAndFiles
    }

    private sealed record JobPathSnapshot(
        DownloadJob Job,
        string FileName,
        string DestinationFolder,
        string? ResolvedTargetPath,
        bool IsActive);

    private readonly DownloadEngine _engine = new();
    private readonly DownloadQueueStore _store = new();
    private readonly PartialDownloadMergeService _mergeService = new();
    private readonly SettingsStore _settingsStore = new();
    private readonly SemaphoreSlim _queueSaveGate = new(1, 1);
    private readonly DispatcherTimer _saveTimer;
    private AppSettings _settings = AppSettings.CreateDefault(KnownFolders.DownloadsDirectory);
    private Task? _initializationTask;
    private bool _queueDirty;
    private bool _isLoaded;
    private bool _isInitialized;
    private bool _isClosing;
    private bool _allowClose;
    private bool _saveInProgress;
    private bool _mergeInProgress;
    private bool _dialogInProgress;
    private bool _removeInProgress;
    private IReadOnlyList<DownloadJob> _contextMenuJobs = [];
    private DownloadJob? _contextMenuAnchorJob;

    public ObservableCollection<DownloadJob> Jobs { get; } = [];

    public MainWindow()
    {
        InitializeComponent();
        DataContext = this;
        Title = AppInfo.WindowTitle;
        FolderBox.Text = _settings.DefaultDownloadDirectory;

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
        UpdateUiState();
        _initializationTask = InitializeAsync();
        try
        {
            await _initializationTask;
        }
        catch (Exception exception)
        {
            DiagnosticLog.Error("UI", "Application data could not be initialized", exception);
            if (!_isClosing)
            {
                MessageBox.Show(
                    this,
                    exception.Message,
                    "无法加载应用数据",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }
        finally
        {
            UpdateUiState();
        }

        if (_isInitialized && !_isClosing)
        {
            UrlBox.Focus();
        }
    }

    private async Task InitializeAsync()
    {
        var settingsTask = _settingsStore.LoadAsync();
        var queueTask = _store.LoadAsync();
        await Task.WhenAll(settingsTask, queueTask);
        _settings = await settingsTask;
        FolderBox.Text = _settings.DefaultDownloadDirectory;
        var restoredJobs = await queueTask;
        foreach (var job in restoredJobs.OrderByDescending(item => item.CreatedAt))
        {
            AttachJob(job);
            Jobs.Add(job);
        }

        _isInitialized = true;
        DiagnosticLog.Info(
            "UI",
            $"Main window loaded; version={AppInfo.Version}; restoredJobs={Jobs.Count}; settingsSchema={_settings.SchemaVersion}");
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.OemComma && Keyboard.Modifiers == ModifierKeys.Control)
        {
            e.Handled = true;
            ShowSettings();
        }
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
        if (!_isInitialized || _mergeInProgress || _dialogInProgress)
        {
            return;
        }

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
            destination = Path.GetFullPath(destination);
            Directory.CreateDirectory(destination);
            FolderBox.Text = destination;
        }
        catch (Exception exception) when (
            exception is ArgumentException or IOException or NotSupportedException or UnauthorizedAccessException)
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

    private async void MergeButton_Click(object sender, RoutedEventArgs e)
    {
        var selected = SelectedJobs();
        if (selected.Count != 2)
        {
            MessageBox.Show("请选择两个需要整合的重复任务。", "无法整合", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        DownloadJob target;
        DownloadJob redundant;
        long targetBytes;
        long redundantBytes;
        try
        {
            var candidates = selected
                .Select(job => (Job: job, Bytes: _mergeService.GetRecoverableBytes(job)))
                .OrderByDescending(item => item.Bytes)
                .ThenBy(item => item.Job.CreatedAt)
                .ToArray();
            (target, targetBytes) = candidates[0];
            (redundant, redundantBytes) = candidates[1];
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException)
        {
            MessageBox.Show(exception.Message, "无法整合", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var previouslyActive = selected
            .Where(job => job.ActiveTask is { IsCompleted: false })
            .OrderByDescending(job => job.CreatedAt)
            .ToList();
        var shouldResume = previouslyActive.Count > 0;
        var linkSource = previouslyActive.FirstOrDefault()
            ?? selected.OrderByDescending(job => job.CreatedAt).First();
        var sourceHost = Uri.TryCreate(linkSource.Url, UriKind.Absolute, out var sourceUri)
            ? sourceUri.Host
            : "未知服务器";
        var targetPath = target.ResolvedTargetPath ?? target.TargetDisplay;
        var redundantPath = redundant.ResolvedTargetPath ?? redundant.TargetDisplay;
        var redundantPartsPath = redundantPath + ".bfdl.parts";
        var afterDescription = shouldResume
            ? "整合成功后会自动继续下载。"
            : "两个任务当前都未下载，整合成功后将保持暂停。";
        var unverifiableBytes = Math.Max(0, targetBytes - redundantBytes);
        var confirmation =
            $"保留任务：\n{targetPath}\n" +
            $"已下载：{DownloadJob.FormatBytes(targetBytes)}\n\n" +
            $"移除重复记录：\n{redundantPath}\n" +
            $"已下载：{DownloadJob.FormatBytes(redundantBytes)}\n\n" +
            $"链接来源：{sourceHost}\n" +
            $"{afterDescription}\n\n" +
            $"可逐字节交叉验证：{DownloadJob.FormatBytes(redundantBytes)}\n" +
            $"无法与新链接完整校验：{DownloadJob.FormatBytes(unverifiableBytes)}\n" +
            "没有发布方提供的整文件 Hash 时，软件无法证明未重叠区域；请确认两个任务确实是同一版本文件。\n\n" +
            $"为保护数据，整合后不会自动删除磁盘上的临时分块：\n{redundantPartsPath}\n" +
            "确认下载完成后可手动清理。";
        if (MessageBox.Show(
                confirmation + "\n\n继续整合？",
                "整合重复任务",
                MessageBoxButton.OKCancel,
                MessageBoxImage.Warning,
                MessageBoxResult.Cancel) != MessageBoxResult.OK)
        {
            return;
        }

        var originalStatuses = selected.ToDictionary(
            job => job.Id,
            job => (State: job.State, Message: job.Message));
        var originalTargetUrl = target.Url;
        var originalTargetEtag = target.ETag;
        var originalTargetLastModified = target.LastModified;
        var originalTargetSupportsRanges = target.SupportsRanges;
        var redundantIndex = Jobs.IndexOf(redundant);
        var redundantDetached = false;
        var consolidated = false;
        _mergeInProgress = true;
        _saveTimer.Stop();
        UpdateUiState();
        var activeTasks = selected
            .Where(job => job.ActiveTask is { IsCompleted: false })
            .Select(job => job.ActiveTask!)
            .ToArray();

        try
        {
            while (_saveInProgress)
            {
                await Task.Delay(25);
            }

            foreach (var job in selected.Where(job => job.ActiveTask is { IsCompleted: false }))
            {
                job.State = DownloadState.Pausing;
                job.Message = "正在暂停以整合任务";
                job.Cancellation?.Cancel();
            }

            await Task.WhenAll(activeTasks);
            target.State = DownloadState.Merging;
            target.Message = "正在验证重复任务的已下载内容";
            redundant.State = DownloadState.Merging;
            redundant.Message = "正在验证重复任务的已下载内容";

            var result = await _mergeService.ValidateRedundantTaskAsync(target, redundant);
            target.Message = "正在验证继续下载所用的链接";
            DownloadProbe probe;
            using (var probeCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30)))
            {
                try
                {
                    probe = await _engine.ProbeAsync(linkSource.Url, probeCancellation.Token);
                }
                catch (OperationCanceledException) when (probeCancellation.IsCancellationRequested)
                {
                    throw new TimeoutException("验证下载链接超过 30 秒，已停止整合；两个任务和分块均已保留。");
                }
            }

            if (probe.TotalBytes != target.TotalBytes)
            {
                throw new InvalidOperationException("新链接报告的文件大小与已有分块不一致，已停止整合。");
            }

            if (!probe.SupportsRanges)
            {
                throw new InvalidOperationException("新链接不支持断点续传，不能用于保留现有分块。");
            }

            if (DownloadEngine.RemoteFileChanged(
                    linkSource.TotalBytes,
                    linkSource.ETag,
                    linkSource.LastModified,
                    probe))
            {
                throw new InvalidOperationException(
                    "下载链接的 ETag 或修改时间已变化，无法确认它仍是创建该任务时的文件；两个任务均已保留。");
            }

            target.Url = linkSource.Url;
            target.ETag = probe.ETag;
            target.LastModified = probe.LastModified;
            target.SupportsRanges = probe.SupportsRanges;
            target.DownloadedBytes = result.RecoveredBytes;
            target.State = DownloadState.Paused;
            target.Message = shouldResume
                ? $"已整合，保留 {DownloadJob.FormatBytes(result.RecoveredBytes)}，正在继续"
                : $"已整合，保留 {DownloadJob.FormatBytes(result.RecoveredBytes)}；旧临时数据需手动清理";

            DetachJob(redundant);
            Jobs.Remove(redundant);
            redundantDetached = true;
            try
            {
                await _store.SaveAsync(Jobs.ToList());
                _queueDirty = true;
                consolidated = true;
            }
            catch (Exception exception)
            {
                AttachJob(redundant);
                Jobs.Insert(Math.Clamp(redundantIndex, 0, Jobs.Count), redundant);
                redundantDetached = false;
                MarkDirty();
                throw new IOException("整合结果无法保存，两个任务记录均已保留。", exception);
            }

            DiagnosticLog.Info(
                "UI",
                $"Duplicate tasks consolidated; target={target.Id:N}; redundant={redundant.Id:N}; " +
                $"linkSource={linkSource.Id:N}; recoveredBytes={result.RecoveredBytes}; " +
                $"comparedBytes={result.ComparedBytes}; redundantPartsDeleted=false");
            QueueGrid.SelectedItem = target;
        }
        catch (Exception exception)
        {
            if (!consolidated)
            {
                if (redundantDetached && !Jobs.Contains(redundant))
                {
                    AttachJob(redundant);
                    Jobs.Insert(Math.Clamp(redundantIndex, 0, Jobs.Count), redundant);
                    redundantDetached = false;
                }

                target.Url = originalTargetUrl;
                target.ETag = originalTargetEtag;
                target.LastModified = originalTargetLastModified;
                target.SupportsRanges = originalTargetSupportsRanges;
                foreach (var job in selected.Where(Jobs.Contains))
                {
                    var original = originalStatuses[job.Id];
                    if (previouslyActive.Contains(job))
                    {
                        job.State = DownloadState.Paused;
                        job.Message = "整合未完成，正在恢复下载";
                    }
                    else
                    {
                        job.State = original.State;
                        job.Message = original.Message;
                    }
                }

                DiagnosticLog.Error("Merge", "Unable to consolidate selected tasks", exception);
                MessageBox.Show(exception.Message, "任务整合失败", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            else
            {
                DiagnosticLog.Error("Merge", "Task records were consolidated but final UI update failed", exception);
                MessageBox.Show(
                    "任务记录已经整合并保存，但界面状态更新失败。重新打开软件即可恢复。",
                    "整合已保存",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
        }
        finally
        {
            _mergeInProgress = false;
            MarkDirty();
            UpdateUiState();
        }

        if (consolidated)
        {
            if (shouldResume)
            {
                _ = StartJobAsync(target);
            }
        }
        else
        {
            foreach (var job in previouslyActive.Where(job => Jobs.Contains(job) && job.State != DownloadState.Completed))
            {
                _ = StartJobAsync(job);
            }
        }
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

    private void SettingsButton_Click(object sender, RoutedEventArgs e) => ShowSettings();

    private void ShowSettings()
    {
        if (!_isInitialized || _dialogInProgress || _mergeInProgress)
        {
            return;
        }

        _dialogInProgress = true;
        UpdateUiState();
        try
        {
            var previousDefault = _settings.DefaultDownloadDirectory;
            var dialog = new SettingsWindow(
                previousDefault,
                directory => _settingsStore.SaveAsync(new AppSettings
                {
                    DefaultDownloadDirectory = directory
                }))
            {
                Owner = this
            };
            if (dialog.ShowDialog() != true)
            {
                return;
            }

            var updatedSettings = new AppSettings
            {
                DefaultDownloadDirectory = dialog.SelectedDirectory
            };
            _settings = updatedSettings;

            if (string.IsNullOrWhiteSpace(FolderBox.Text)
                || PathsEqual(FolderBox.Text, previousDefault))
            {
                FolderBox.Text = updatedSettings.DefaultDownloadDirectory;
            }

            DiagnosticLog.Info("UI", "Default download directory updated");
        }
        catch (Exception exception) when (
            exception is ArgumentException or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            DiagnosticLog.Error("Settings", "Unable to save settings from the UI", exception);
            MessageBox.Show(this, exception.Message, "无法保存设置", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _dialogInProgress = false;
            UpdateUiState();
        }
    }

    private void AboutButton_Click(object sender, RoutedEventArgs e)
    {
        if (_dialogInProgress || _mergeInProgress)
        {
            return;
        }

        _dialogInProgress = true;
        UpdateUiState();
        try
        {
            var dialog = new AboutWindow { Owner = this };
            dialog.ShowDialog();
        }
        finally
        {
            _dialogInProgress = false;
            UpdateUiState();
        }
    }

    private async void RemoveButton_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedJob() is not { } job)
        {
            return;
        }

        await RemoveJobsAsync([job], RemovalMode.RecordOnly);
    }

    private void QueueGrid_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject source
            || ItemsControl.ContainerFromElement(QueueGrid, source) is not DataGridRow row)
        {
            return;
        }

        SelectContextRow(QueueGrid, row);
    }

    private void TaskContextMenu_Opened(object sender, RoutedEventArgs e)
    {
        _contextMenuJobs = [];
        _contextMenuAnchorJob = null;
        if (sender is not ContextMenu contextMenu)
        {
            return;
        }

        var row = contextMenu.PlacementTarget as DataGridRow
            ?? (contextMenu.PlacementTarget is DependencyObject placementTarget
                ? ItemsControl.ContainerFromElement(QueueGrid, placementTarget) as DataGridRow
                : null);
        if (row is null
            || row.Item is not DownloadJob job
            || !Jobs.Contains(job))
        {
            contextMenu.IsOpen = false;
            return;
        }

        SelectContextRow(QueueGrid, row);

        _contextMenuJobs = SelectedJobs()
            .Where(Jobs.Contains)
            .DistinctBy(item => item.Id)
            .ToArray();
        _contextMenuAnchorJob = job;
        var canInteract = _isInitialized && !_isClosing && !_mergeInProgress && !_dialogInProgress;
        var menuItems = contextMenu.Items.OfType<MenuItem>().ToArray();
        if (menuItems.Length >= 2)
        {
            menuItems[0].IsEnabled = canInteract;
            menuItems[1].IsEnabled = canInteract && _contextMenuJobs.Count > 0;
        }
    }

    internal static void SelectContextRow(DataGrid queueGrid, DataGridRow row)
    {
        if (!row.IsSelected)
        {
            queueGrid.SelectedItems.Clear();
            row.IsSelected = true;
        }

        row.Focus();
    }

    private void OpenLocationMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (_contextMenuAnchorJob is { } job && Jobs.Contains(job))
        {
            OpenJobLocation(job);
        }
    }

    private async void DeleteRecordMenuItem_Click(object sender, RoutedEventArgs e)
    {
        var jobs = _contextMenuJobs.ToArray();
        await RemoveJobsAsync(jobs, RemovalMode.RecordOnly);
    }

    private async void DeleteFilesMenuItem_Click(object sender, RoutedEventArgs e)
    {
        var jobs = _contextMenuJobs.ToArray();
        await RemoveJobsAsync(jobs, RemovalMode.RecordAndFiles);
    }

    private async Task RemoveJobsAsync(IReadOnlyList<DownloadJob> requestedJobs, RemovalMode mode)
    {
        if (!_isInitialized || _isClosing || _mergeInProgress || _dialogInProgress || _removeInProgress)
        {
            return;
        }

        var jobs = requestedJobs
            .Where(Jobs.Contains)
            .DistinctBy(job => job.Id)
            .ToList();
        if (jobs.Count == 0)
        {
            return;
        }

        _removeInProgress = true;
        _dialogInProgress = true;
        UpdateUiState();
        try
        {
            IReadOnlyDictionary<DownloadJob, DownloadArtifactPlan?> initialPlans =
                new Dictionary<DownloadJob, DownloadArtifactPlan?>();
            if (mode == RemovalMode.RecordAndFiles)
            {
                var builtPlans = await TryBuildDeletionPlansAsync(jobs);
                if (builtPlans is null)
                {
                    return;
                }

                initialPlans = builtPlans;
            }

            if (MessageBox.Show(
                    this,
                    BuildRemovalConfirmation(jobs, mode, initialPlans, pathsChanged: false),
                    mode == RemovalMode.RecordOnly ? "仅删除任务记录" : "删除任务记录及文件",
                    MessageBoxButton.OKCancel,
                    mode == RemovalMode.RecordOnly ? MessageBoxImage.Question : MessageBoxImage.Warning,
                    MessageBoxResult.Cancel) != MessageBoxResult.OK)
            {
                return;
            }

            if (!await StopJobsForRemovalAsync(jobs))
            {
                return;
            }

            var finalPlans = initialPlans;
            if (mode == RemovalMode.RecordAndFiles)
            {
                var builtPlans = await TryBuildDeletionPlansAsync(jobs);
                if (builtPlans is null)
                {
                    return;
                }

                finalPlans = builtPlans;

                if (DeletionPlansChanged(jobs, initialPlans, finalPlans)
                    && MessageBox.Show(
                        this,
                        BuildRemovalConfirmation(jobs, mode, finalPlans, pathsChanged: true),
                        "文件路径已更新，请再次确认",
                        MessageBoxButton.OKCancel,
                        MessageBoxImage.Warning,
                        MessageBoxResult.Cancel) != MessageBoxResult.OK)
                {
                    return;
                }
            }

            var deletionSnapshots = new Dictionary<DownloadJob, (DownloadState State, string Message, long DownloadedBytes)>();
            if (mode == RemovalMode.RecordAndFiles)
            {
                deletionSnapshots = jobs.ToDictionary(
                    job => job,
                    job => (job.State, job.Message, job.DownloadedBytes));
                foreach (var job in jobs)
                {
                    job.State = DownloadState.Deleting;
                    job.Message = "正在准备删除任务文件";
                    job.BytesPerSecond = 0;
                }

                MarkDirty();
                UpdateUiState();
                if (!await SaveQueueAsync())
                {
                    RestoreJobStates(deletionSnapshots);
                    DiagnosticLog.Warning("UI", "File deletion aborted because the deletion checkpoint could not be saved");
                    MessageBox.Show(
                        this,
                        "任务列表无法保存。为保护磁盘文件，删除操作尚未执行。",
                        "无法开始删除",
                        MessageBoxButton.OK,
                        MessageBoxImage.Error);
                    return;
                }

                var executionPlans = await TryBuildDeletionPlansAsync(jobs);
                if (executionPlans is null)
                {
                    RestoreJobStates(deletionSnapshots);
                    await SaveQueueAsync();
                    return;
                }

                if (DeletionPlansChanged(jobs, finalPlans, executionPlans))
                {
                    RestoreJobStates(deletionSnapshots);
                    await SaveQueueAsync();
                    DiagnosticLog.Warning("UI", "File deletion aborted because artifact paths changed after checkpoint");
                    MessageBox.Show(
                        this,
                        "写入删除检查点后，任务文件或保存位置发生了变化。为避免误删，本次操作已取消，请重新执行。",
                        "文件位置已变化",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                    return;
                }

                finalPlans = executionPlans;
            }

            var failures = new Dictionary<DownloadJob, string>();
            var preservedTargetPaths = new List<string>();
            var successfulJobs = mode == RemovalMode.RecordOnly
                ? jobs.ToHashSet()
                : await Task.Run(() => DeleteJobData(finalPlans, failures, preservedTargetPaths));
            MarkFailedDeletionJobs(failures.Keys);
            if (successfulJobs.Count == 0)
            {
                MarkDirty();
                UpdateUiState();
                await SaveQueueAsync();
                ShowDeletionFailures(failures, removedCount: 0);
                return;
            }

            var removedJobs = successfulJobs
                .Select(job => (Job: job, Index: Jobs.IndexOf(job)))
                .Where(item => item.Index >= 0)
                .OrderBy(item => item.Index)
                .ToArray();
            foreach (var (job, _) in removedJobs)
            {
                DetachJob(job);
                Jobs.Remove(job);
            }

            MarkDirty();
            UpdateUiState();
            if (!await SaveQueueAsync())
            {
                if (mode == RemovalMode.RecordAndFiles)
                {
                    foreach (var (job, _) in removedJobs)
                    {
                        if (finalPlans.TryGetValue(job, out var plan) && plan is not null)
                        {
                            MarkFailedDeletionJobs([job]);
                        }
                        else if (deletionSnapshots.TryGetValue(job, out var snapshot))
                        {
                            RestoreJobState(job, snapshot);
                        }
                    }
                }

                RestoreRemovedJobs(removedJobs);
                DiagnosticLog.Warning(
                    "UI",
                    $"Task removal rolled back after queue save failure; count={removedJobs.Length}; filesDeleted={mode == RemovalMode.RecordAndFiles}");
                MessageBox.Show(
                    this,
                    mode == RemovalMode.RecordOnly
                        ? "任务列表保存失败，删除记录操作已撤销。"
                        : "任务列表保存失败，任务记录已恢复。已处理的磁盘文件可能已移入回收站或删除，请查看任务状态和诊断日志。",
                    "无法保存任务列表",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
                return;
            }

            DiagnosticLog.Info(
                "UI",
                $"Tasks removed; count={removedJobs.Length}; filesDeleted={mode == RemovalMode.RecordAndFiles}; failures={failures.Count}");
            if (failures.Count > 0)
            {
                ShowDeletionFailures(failures, removedJobs.Length);
            }

            if (preservedTargetPaths.Count > 0)
            {
                ShowPreservedTargetFiles(preservedTargetPaths);
            }
        }
        finally
        {
            _contextMenuJobs = [];
            _contextMenuAnchorJob = null;
            _removeInProgress = false;
            _dialogInProgress = false;
            UpdateUiState();
        }
    }

    private static void MarkFailedDeletionJobs(IEnumerable<DownloadJob> jobs)
    {
        foreach (var job in jobs)
        {
            job.State = DownloadState.DeletionFailed;
            job.BytesPerSecond = 0;
            job.Message = "文件删除未完成，记录已保留；请通过右键菜单重试删除或仅删除记录";
        }
    }

    private void RestoreJobStates(
        IReadOnlyDictionary<DownloadJob, (DownloadState State, string Message, long DownloadedBytes)> snapshots)
    {
        foreach (var (job, snapshot) in snapshots)
        {
            RestoreJobState(job, snapshot);
        }

        MarkDirty();
        UpdateUiState();
    }

    private static void RestoreJobState(
        DownloadJob job,
        (DownloadState State, string Message, long DownloadedBytes) snapshot)
    {
        job.State = snapshot.State;
        job.Message = snapshot.Message;
        job.DownloadedBytes = snapshot.DownloadedBytes;
    }

    private async Task<IReadOnlyDictionary<DownloadJob, DownloadArtifactPlan?>?> TryBuildDeletionPlansAsync(
        IReadOnlyList<DownloadJob> selectedJobs)
    {
        var snapshots = Jobs
            .Select(job => new JobPathSnapshot(
                job,
                job.FileName,
                job.DestinationFolder,
                job.ResolvedTargetPath,
                job.ActiveTask is { IsCompleted: false }))
            .ToArray();
        try
        {
            return await Task.Run(() => BuildDeletionPlans(selectedJobs, snapshots));
        }
        catch (Exception exception) when (
            exception is ArgumentException or IOException or InvalidOperationException or NotSupportedException
                or UnauthorizedAccessException)
        {
            DiagnosticLog.Error("UI", "Task file deletion was blocked by path validation", exception);
            MessageBox.Show(
                this,
                exception.Message + "\n\n未删除任何文件。你仍可选择“仅删除任务记录”。",
                "无法安全删除文件",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return null;
        }
    }

    private static IReadOnlyDictionary<DownloadJob, DownloadArtifactPlan?> BuildDeletionPlans(
        IReadOnlyList<DownloadJob> selectedJobs,
        IReadOnlyList<JobPathSnapshot> snapshots)
    {
        var selectedSet = selectedJobs.ToHashSet();
        var snapshotsByJob = snapshots.ToDictionary(snapshot => snapshot.Job);
        var selectedPlans = new Dictionary<DownloadJob, DownloadArtifactPlan?>();
        foreach (var job in selectedJobs)
        {
            if (!snapshotsByJob.TryGetValue(job, out var snapshot))
            {
                throw new InvalidOperationException("待删除任务已不在任务列表中。");
            }

            try
            {
                selectedPlans[job] = DownloadArtifactPlan.CreateFor(
                    snapshot.DestinationFolder,
                    snapshot.FileName,
                    snapshot.ResolvedTargetPath);
            }
            catch (Exception exception) when (
                exception is ArgumentException or IOException or InvalidOperationException or NotSupportedException
                    or UnauthorizedAccessException)
            {
                throw new InvalidOperationException(
                    $"任务“{job.FileName}”的路径无法安全验证：{exception.Message}",
                    exception);
            }
        }

        var resolvedSelectedPlans = selectedPlans
            .Where(item => item.Value is not null)
            .Select(item => (item.Key, Plan: item.Value!))
            .ToArray();
        for (var leftIndex = 0; leftIndex < resolvedSelectedPlans.Length; leftIndex++)
        {
            for (var rightIndex = leftIndex + 1; rightIndex < resolvedSelectedPlans.Length; rightIndex++)
            {
                var left = resolvedSelectedPlans[leftIndex];
                var right = resolvedSelectedPlans[rightIndex];
                if (left.Plan.Overlaps(right.Plan) && !left.Plan.HasSameArtifacts(right.Plan))
                {
                    throw new InvalidOperationException(
                        $"任务“{left.Key.FileName}”和“{right.Key.FileName}”的磁盘数据相互重叠，不能自动删除。");
                }
            }
        }

        foreach (var retained in snapshots.Where(snapshot => !selectedSet.Contains(snapshot.Job)))
        {
            if (string.IsNullOrWhiteSpace(retained.ResolvedTargetPath))
            {
                if (retained.IsActive)
                {
                    var pendingDestination = TryNormalizeDirectory(retained.DestinationFolder)
                        ?? throw new InvalidOperationException(
                            $"无法验证正在运行的任务“{retained.FileName}”的保存位置。请先暂停该任务。");
                    if (resolvedSelectedPlans.Any(
                        selected => selected.Plan.ConflictsWithPendingDestination(pendingDestination)))
                    {
                        throw new InvalidOperationException(
                            $"任务“{retained.FileName}”正在可能重叠的保存位置中确定文件路径。请等待它开始下载或先暂停，再删除文件。");
                    }
                }

                continue;
            }

            var retainedPlan = TryCreatePotentialPlan(retained.ResolvedTargetPath);
            if (retainedPlan is null)
            {
                throw new InvalidOperationException(
                    $"无法验证保留任务“{retained.FileName}”的实际文件位置。为避免误删，不能删除磁盘文件。");
            }

            var shared = resolvedSelectedPlans.FirstOrDefault(selected => selected.Plan.Overlaps(retainedPlan));
            if (shared.Key is not null)
            {
                throw new InvalidOperationException(
                    $"任务“{shared.Key.FileName}”的磁盘数据还被“{retained.FileName}”引用，不能删除共享文件。");
            }
        }

        return selectedPlans;
    }

    private static DownloadArtifactPlan? TryCreatePotentialPlan(string targetPath)
    {
        try
        {
            return DownloadArtifactPlan.CreateForTargetPath(targetPath);
        }
        catch (Exception exception) when (
            exception is ArgumentException or IOException or InvalidOperationException or NotSupportedException
                or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string? TryNormalizeDirectory(string directory)
    {
        try
        {
            return Path.TrimEndingDirectorySeparator(
                PhysicalPathResolver.ResolveForComparison(Path.GetFullPath(directory)));
        }
        catch (Exception exception) when (
            exception is ArgumentException or IOException or NotSupportedException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string BuildRemovalConfirmation(
        IReadOnlyList<DownloadJob> jobs,
        RemovalMode mode,
        IReadOnlyDictionary<DownloadJob, DownloadArtifactPlan?> plans,
        bool pathsChanged)
    {
        var subject = jobs.Count == 1 ? $"“{jobs[0].FileName}”" : $"{jobs.Count} 个任务";
        var activeText = jobs.Any(job => job.ActiveTask is { IsCompleted: false })
            ? "下载连接会先安全停止。\n"
            : string.Empty;
        if (mode == RemovalMode.RecordOnly)
        {
            return
                $"从任务列表删除{subject}？\n\n" +
                activeText +
                "磁盘上的成品文件和断点续传临时数据都会保留。\n" +
                "删除记录后，程序将无法从任务列表继续这些临时数据。\n\n" +
                "确认仅删除任务记录？";
        }

        var targetPaths = plans.Values
            .OfType<DownloadArtifactPlan>()
            .Select(plan => plan.TargetPath)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var unresolvedCount = plans.Values.Count(plan => plan is null);
        var pathText = targetPaths.Length == 0
            ? "这些任务尚未确定文件路径，确认后只会删除记录。"
            : "目标路径：\n" + FormatPathList(targetPaths);
        if (unresolvedCount > 0 && targetPaths.Length > 0)
        {
            pathText += $"\n另有 {unresolvedCount} 个任务尚未确定文件路径。";
        }

        var changedText = pathsChanged
            ? "停止任务后，实际文件路径发生了变化，请重新核对。\n\n"
            : string.Empty;
        return
            changedText +
            $"删除{subject}的任务记录及文件？\n\n" +
            activeText +
            "成品文件将移入 Windows 回收站；不支持回收站的位置可能直接删除。\n" +
            "断点续传分块和其他临时数据将永久删除。\n" +
            "保存文件夹及其中其他文件不会删除。\n" +
            "只有能核实为本任务创建且之后未被替换的成品才会删除；旧任务、外部文件或身份无法核实的成品会保留。\n\n" +
            pathText +
            "\n\n确认执行？";
    }

    private static string FormatPathList(IReadOnlyList<string> paths)
    {
        const int maximumDisplayedPaths = 6;
        var lines = paths.Take(maximumDisplayedPaths).Select(path => $"• {path}").ToList();
        if (paths.Count > maximumDisplayedPaths)
        {
            lines.Add($"• 另外 {paths.Count - maximumDisplayedPaths} 个路径");
        }

        return string.Join(Environment.NewLine, lines);
    }

    private async Task<bool> StopJobsForRemovalAsync(IReadOnlyList<DownloadJob> jobs)
    {
        var active = jobs
            .Where(job => job.ActiveTask is { IsCompleted: false })
            .Select(job => (Job: job, Task: job.ActiveTask!))
            .ToArray();
        if (active.Length == 0)
        {
            return true;
        }

        if (active.Any(item => item.Job.Cancellation is null))
        {
            MessageBox.Show(
                this,
                "有任务仍在运行，但无法安全停止。未删除任务或文件，请稍后重试。",
                "无法停止任务",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return false;
        }

        try
        {
            foreach (var (job, _) in active)
            {
                job.State = DownloadState.Pausing;
                job.Message = "正在安全停止以执行删除";
                job.Cancellation!.Cancel();
            }

            await Task.WhenAll(active.Select(item => item.Task));
            return true;
        }
        catch (Exception exception)
        {
            DiagnosticLog.Error("UI", "Unable to stop tasks before removal", exception);
            MessageBox.Show(
                this,
                "任务未能安全停止，因此没有删除任何记录或文件。\n\n" + exception.Message,
                "无法停止任务",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return false;
        }
    }

    private static bool DeletionPlansChanged(
        IReadOnlyList<DownloadJob> jobs,
        IReadOnlyDictionary<DownloadJob, DownloadArtifactPlan?> initialPlans,
        IReadOnlyDictionary<DownloadJob, DownloadArtifactPlan?> finalPlans)
    {
        return jobs.Any(job =>
        {
            initialPlans.TryGetValue(job, out var initial);
            finalPlans.TryGetValue(job, out var final);
            return initial is null != (final is null)
                || initial is not null && final is not null && !initial.HasSameArtifacts(final);
        });
    }

    private HashSet<DownloadJob> DeleteJobData(
        IReadOnlyDictionary<DownloadJob, DownloadArtifactPlan?> plans,
        IDictionary<DownloadJob, string> failures,
        ICollection<string> preservedTargetPaths)
    {
        var successful = plans
            .Where(item => item.Value is null)
            .Select(item => item.Key)
            .ToHashSet();
        var planGroups = plans
            .Where(item => item.Value is not null)
            .GroupBy(item => item.Value!.ArtifactIdentityKey, StringComparer.Ordinal);
        foreach (var group in planGroups)
        {
            var groupJobs = group.Select(item => item.Key).ToArray();
            try
            {
                var deletionPlan = group.First().Value!;
                var result = _engine.DeleteDownloadData(groupJobs, deletionPlan);
                if (result.PreservedTargetPath is not null)
                {
                    preservedTargetPaths.Add(result.PreservedTargetPath);
                }

                successful.UnionWith(groupJobs);
            }
            catch (Exception exception)
            {
                DiagnosticLog.Error(
                    "UI",
                    $"Task files could not be fully deleted; target={group.First().Value!.TargetPath}; " +
                    $"jobs={string.Join(',', groupJobs.Select(job => job.Id.ToString("N")))}",
                    exception);
                foreach (var job in groupJobs)
                {
                    failures[job] = exception.Message;
                }
            }
        }

        return successful;
    }

    private void ShowPreservedTargetFiles(IReadOnlyCollection<string> paths)
    {
        var distinctPaths = paths.Distinct(StringComparer.Ordinal).ToArray();
        MessageBox.Show(
            this,
            "以下目标已确认不是由对应下载任务创建，因此未删除。任务记录和任务临时数据已处理。\n\n" +
            FormatPathList(distinctPaths),
            "已保留外部文件",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
    }

    private void ShowDeletionFailures(IReadOnlyDictionary<DownloadJob, string> failures, int removedCount)
    {
        if (failures.Count == 0)
        {
            return;
        }

        var details = failures
            .Take(5)
            .Select(item =>
                $"• {item.Key.FileName}\n  {item.Key.ResolvedTargetPath ?? "尚未确定路径"}\n  {item.Value}")
            .ToList();
        if (failures.Count > details.Count)
        {
            details.Add($"• 另外 {failures.Count - details.Count} 个任务未处理");
        }

        var summary = removedCount > 0
            ? $"已删除 {removedCount} 个任务；另有 {failures.Count} 个任务的文件未能完整处理，记录已保留。"
            : $"{failures.Count} 个任务的文件未能完整处理，任务记录均已保留。";
        MessageBox.Show(
            this,
            summary + "\n\n" + string.Join("\n\n", details),
            "部分文件未删除",
            MessageBoxButton.OK,
            MessageBoxImage.Warning);
    }

    private void RestoreRemovedJobs(IEnumerable<(DownloadJob Job, int Index)> removedJobs)
    {
        foreach (var (job, index) in removedJobs.OrderBy(item => item.Index))
        {
            AttachJob(job);
            Jobs.Insert(Math.Min(index, Jobs.Count), job);
        }

        MarkDirty();
        UpdateUiState();
    }

    private async void ClearCompletedButton_Click(object sender, RoutedEventArgs e)
    {
        var completed = Jobs.Where(job => job.State == DownloadState.Completed).ToList();
        if (completed.Count == 0)
        {
            return;
        }

        await RemoveJobsAsync(completed, RemovalMode.RecordOnly);
    }

    private void QueueGrid_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateUiState();

    private void QueueGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        var selected = SelectedJobs();
        if (selected.Count == 1)
        {
            OpenJobLocation(selected[0]);
        }
    }

    private void OpenJobLocation(DownloadJob job)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(job.ResolvedTargetPath)
                && Path.IsPathFullyQualified(job.ResolvedTargetPath)
                && File.Exists(job.ResolvedTargetPath))
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{job.ResolvedTargetPath}\"") { UseShellExecute = true });
            }
            else if (Path.IsPathFullyQualified(job.DestinationFolder) && Directory.Exists(job.DestinationFolder))
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{job.DestinationFolder}\"") { UseShellExecute = true });
            }
            else
            {
                MessageBox.Show(
                    this,
                    "任务记录的保存位置不存在或不是绝对路径。",
                    "无法打开文件位置",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
        }
        catch (Exception exception) when (
            exception is ArgumentException or IOException or InvalidOperationException or NotSupportedException
                or UnauthorizedAccessException or Win32Exception)
        {
            DiagnosticLog.Error("UI", $"Unable to open task location; job={job.Id:N}", exception);
            MessageBox.Show(exception.Message, "无法打开文件夹", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_removeInProgress)
        {
            e.Cancel = true;
            MessageBox.Show("正在安全停止任务或删除文件，请等待操作完成。", "正在处理删除", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (_mergeInProgress)
        {
            e.Cancel = true;
            MessageBox.Show("任务正在合并，请等待操作完成后再关闭。", "正在合并", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

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
        if (!_isInitialized && _initializationTask is not null)
        {
            try
            {
                await _initializationTask;
            }
            catch
            {
                // Initialization failure has already been logged by Window_Loaded.
            }
        }

        if (!_isInitialized)
        {
            DiagnosticLog.Warning("UI", "Shutdown skipped queue save because initialization did not complete");
            _allowClose = true;
            Close();
            return;
        }

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

    private async Task<bool> SaveQueueAsync()
    {
        if (_mergeInProgress)
        {
            _queueDirty = true;
            return false;
        }

        await _queueSaveGate.WaitAsync();
        try
        {
            if (_mergeInProgress)
            {
                _queueDirty = true;
                return false;
            }

            if (!_queueDirty)
            {
                return true;
            }

            var snapshot = Jobs.ToList();
            _queueDirty = false;
            _saveInProgress = true;
            try
            {
                await _store.SaveAsync(snapshot);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                _queueDirty = true;
                DiagnosticLog.Error("Queue", "Unable to save task queue", exception);
                StorageText.Text = $"任务列表保存失败：{exception.Message}";
                return false;
            }
            finally
            {
                _saveInProgress = false;
            }

            return true;
        }
        finally
        {
            _queueSaveGate.Release();
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
        if (!_mergeInProgress && !_saveTimer.IsEnabled)
        {
            _saveTimer.Start();
        }
    }

    private DownloadJob? SelectedJob() => QueueGrid.SelectedItem as DownloadJob;

    private IReadOnlyList<DownloadJob> SelectedJobs() => QueueGrid.SelectedItems.OfType<DownloadJob>().ToList();

    private int SelectedSegmentCount()
    {
        return SegmentBox.SelectedItem is ComboBoxItem { Tag: string value }
            && int.TryParse(value, out var result)
                ? result
                : 4;
    }

    private void UpdateUiState()
    {
        var selectedJobs = SelectedJobs();
        var selected = selectedJobs.Count == 1 ? selectedJobs[0] : null;
        var canInteract = _isInitialized && !_isClosing && !_mergeInProgress && !_dialogInProgress;
        UrlBox.IsEnabled = canInteract;
        FolderBox.IsEnabled = canInteract;
        BrowseButton.IsEnabled = canInteract;
        SegmentBox.IsEnabled = canInteract;
        AddButton.IsEnabled = canInteract;
        QueueGrid.IsEnabled = canInteract;
        PauseButton.IsEnabled = canInteract
            && selected?.State is DownloadState.Inspecting or DownloadState.Downloading or DownloadState.Merging;
        ResumeButton.IsEnabled = canInteract && selected?.State is DownloadState.Pending or DownloadState.Paused;
        RetryButton.IsEnabled = canInteract && selected?.State == DownloadState.Failed;
        UpdateLinkButton.IsEnabled = canInteract
            && selected?.State is DownloadState.Pending or DownloadState.Paused or DownloadState.Failed;
        MergeButton.IsEnabled = canInteract && selectedJobs.Count == 2;
        OpenButton.IsEnabled = canInteract && selected is not null;
        RemoveButton.IsEnabled = canInteract && selected is not null;
        ClearCompletedButton.IsEnabled = canInteract && Jobs.Any(job => job.State == DownloadState.Completed);
        LogButton.IsEnabled = !_isClosing && !_dialogInProgress;
        SettingsButton.IsEnabled = canInteract;
        AboutButton.IsEnabled = canInteract;
        EmptyState.Visibility = _isInitialized && Jobs.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        if (!_isInitialized)
        {
            SummaryText.Text = _initializationTask?.IsFaulted == true
                ? "任务列表加载失败，请查看诊断日志"
                : "正在加载任务列表...";
            StorageText.Text = string.Empty;
            return;
        }

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

    private static bool PathsEqual(string first, string second)
    {
        try
        {
            return string.Equals(
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(first)),
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(second)),
                StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
        {
            return string.Equals(first.Trim(), second.Trim(), StringComparison.OrdinalIgnoreCase);
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
