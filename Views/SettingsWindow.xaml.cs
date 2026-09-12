using System.Windows;
using System.Windows.Controls;
using BigFileDownloader.Services;
using Microsoft.Win32;

namespace BigFileDownloader.Views;

public partial class SettingsWindow : Window
{
    private readonly Func<string, Task> _saveAsync;
    private bool _saveInProgress;
    private bool _allowClose;

    public SettingsWindow(string defaultDownloadDirectory, Func<string, Task>? saveAsync = null)
    {
        InitializeComponent();
        _saveAsync = saveAsync ?? (_ => Task.CompletedTask);
        Title = $"设置 - {AppInfo.WindowTitle}";
        DefaultDirectoryBox.Text = defaultDownloadDirectory;
        SelectedDirectory = defaultDownloadDirectory;
        Loaded += (_, _) =>
        {
            DefaultDirectoryBox.Focus();
            DefaultDirectoryBox.CaretIndex = DefaultDirectoryBox.Text.Length;
        };
    }

    public string SelectedDirectory { get; private set; }

    private void BrowseButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "选择默认下载位置",
            InitialDirectory = Directory.Exists(DefaultDirectoryBox.Text)
                ? DefaultDirectoryBox.Text
                : ApplicationDataPaths.DefaultDownloadDirectory
        };

        if (dialog.ShowDialog(this) == true)
        {
            DefaultDirectoryBox.Text = dialog.FolderName;
            DefaultDirectoryBox.CaretIndex = DefaultDirectoryBox.Text.Length;
        }
    }

    private void RestoreDefaultButton_Click(object sender, RoutedEventArgs e)
    {
        DefaultDirectoryBox.Text = ApplicationDataPaths.DefaultDownloadDirectory;
        DefaultDirectoryBox.Focus();
        DefaultDirectoryBox.CaretIndex = DefaultDirectoryBox.Text.Length;
    }

    private async void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        if (await TrySaveAsync())
        {
            _allowClose = true;
            DialogResult = true;
        }
    }

    internal async Task<bool> TrySaveAsync()
    {
        if (_saveInProgress)
        {
            return false;
        }

        var value = DefaultDirectoryBox.Text.Trim();
        try
        {
            if (string.IsNullOrWhiteSpace(value) || !Path.IsPathFullyQualified(value))
            {
                throw new ArgumentException("请输入有效的绝对文件夹路径。");
            }

            var directory = Path.GetFullPath(value);
            Directory.CreateDirectory(directory);
            _saveInProgress = true;
            SetEditingEnabled(false);
            await _saveAsync(directory);
            SelectedDirectory = directory;
            return true;
        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException or InvalidOperationException
                or IOException or UnauthorizedAccessException)
        {
            DiagnosticLog.Error("Settings", "Default download directory could not be saved", exception);
            ErrorText.Text = exception is ArgumentException or NotSupportedException
                ? "请输入有效的绝对文件夹路径。"
                : "无法使用或保存此位置，请检查访问权限和可用空间。";
            ErrorText.ToolTip = exception.Message;
            ErrorText.Visibility = Visibility.Visible;
            return false;
        }
        finally
        {
            _saveInProgress = false;
            SetEditingEnabled(true);
            if (ErrorText.Visibility == Visibility.Visible)
            {
                DefaultDirectoryBox.Focus();
            }
        }
    }

    private void DefaultDirectoryBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (ErrorText is not null)
        {
            ErrorText.Text = string.Empty;
            ErrorText.ToolTip = null;
            ErrorText.Visibility = Visibility.Collapsed;
        }
    }

    private void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_saveInProgress && !_allowClose)
        {
            e.Cancel = true;
        }
    }

    private void SetEditingEnabled(bool isEnabled)
    {
        DefaultDirectoryBox.IsEnabled = isEnabled;
        BrowseButton.IsEnabled = isEnabled;
        RestoreDefaultButton.IsEnabled = isEnabled;
        CancelButton.IsEnabled = isEnabled;
        SaveButton.IsEnabled = isEnabled;
    }
}
