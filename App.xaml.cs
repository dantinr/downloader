using System.Security.Principal;
using System.Windows;
using System.Windows.Threading;
using BigFileDownloader.Services;

namespace BigFileDownloader;

public partial class App : Application
{
    private static readonly string SingleInstanceMutexName = BuildSingleInstanceMutexName();
    private Mutex? _singleInstanceMutex;
    private bool _ownsSingleInstanceMutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        DiagnosticLog.Initialize();
        _singleInstanceMutex = new Mutex(initiallyOwned: false, SingleInstanceMutexName);
        try
        {
            _ownsSingleInstanceMutex = _singleInstanceMutex.WaitOne(0);
        }
        catch (AbandonedMutexException)
        {
            _ownsSingleInstanceMutex = true;
        }

        if (!_ownsSingleInstanceMutex)
        {
            DiagnosticLog.Warning("App", "A second application instance was blocked");
            MessageBox.Show(
                "downloader 已在运行，请切换到已有窗口。",
                AppInfo.WindowTitle,
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            Shutdown();
            return;
        }

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        base.OnStartup(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_ownsSingleInstanceMutex)
        {
            try
            {
                _singleInstanceMutex?.ReleaseMutex();
            }
            catch (ApplicationException exception)
            {
                DiagnosticLog.Warning("App", $"Single-instance mutex release failed: {exception.Message}");
            }
        }

        _singleInstanceMutex?.Dispose();
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

    private static string BuildSingleInstanceMutexName()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var userScope = identity.User?.Value ?? Environment.UserName;
        return $@"Global\BigFileDownloader.Downloader.{userScope}";
    }
}
