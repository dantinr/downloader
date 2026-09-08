using System.Windows;
using System.Windows.Threading;
using BigFileDownloader.Services;

namespace BigFileDownloader;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        DiagnosticLog.Initialize();
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        base.OnStartup(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        DiagnosticLog.Shutdown();
        base.OnExit(e);
    }

    private static void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        DiagnosticLog.Error("App", "Unhandled UI exception", e.Exception);
        MessageBox.Show(
            e.Exception.Message,
            AppInfo.WindowTitle,
            MessageBoxButton.OK,
            MessageBoxImage.Error);
        e.Handled = true;
    }
}
