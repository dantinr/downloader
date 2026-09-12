using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using BigFileDownloader.Services;

namespace BigFileDownloader.Views;

public partial class AboutWindow : Window
{
    public AboutWindow()
    {
        InitializeComponent();
        Title = $"关于 - {AppInfo.WindowTitle}";
        ProductNameText.Text = AppInfo.ProductName;
        VersionText.Text = $"版本 {AppInfo.Version}";
        DescriptionText.Text = AppInfo.Description;
        LicenseText.Text = AppInfo.LicenseName;
        RepositoryText.Text = AppInfo.RepositoryUrl;
        SystemRequirementsText.Text = AppInfo.SystemRequirements;
        Loaded += (_, _) => CloseButton.Focus();
    }

    private void RepositoryButton_Click(object sender, RoutedEventArgs e) => OpenUrl(AppInfo.RepositoryUrl);

    private void LicenseButton_Click(object sender, RoutedEventArgs e)
    {
        var localLicense = Path.Combine(AppContext.BaseDirectory, "LICENSE");
        OpenTarget(File.Exists(localLicense) ? localLicense : AppInfo.LicenseUrl);
    }

    private void CopyDiagnosticsButton_Click(object sender, RoutedEventArgs e)
    {
        var diagnostics =
            $"{AppInfo.ProductName} {AppInfo.Version}{Environment.NewLine}" +
            $"{RuntimeInformation.OSDescription}{Environment.NewLine}" +
            $"{RuntimeInformation.FrameworkDescription} ({RuntimeInformation.ProcessArchitecture})";
        try
        {
            Clipboard.SetText(diagnostics);
            CopyDiagnosticsText.Text = "已复制";
        }
        catch (ExternalException exception)
        {
            CopyDiagnosticsText.Text = "复制信息";
            MessageBox.Show(this, exception.Message, "无法复制诊断信息", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void OpenUrl(string url) => OpenTarget(url);

    private void OpenTarget(string target)
    {
        try
        {
            Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or Win32Exception)
        {
            MessageBox.Show(this, exception.Message, "无法打开链接", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}
