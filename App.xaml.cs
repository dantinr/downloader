using System.Security.Principal;
using System.Windows;
using System.Windows.Threading;
using BigFileDownloader.Services;

namespace BigFileDownloader;

public partial class App : Application
{
    private Mutex? _singleInstanceMutex;
    private bool _ownsSingleInstanceMutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        _singleInstanceMutex = new Mutex(initiallyOwned: false, BuildSingleInstanceMutexName());
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
            DiagnosticLog.Initialize();
            DiagnosticLog.Warning("App", "A second application instance was blocked");
            MessageBox.Show(
                "downloader 已在运行，请切换到已有窗口。",
                AppInfo.WindowTitle,
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            Shutdown();
            return;
        }

        var migration = ApplicationDataPaths.MigrateLegacyData();
        DiagnosticLog.Initialize();
        if (migration.MigratedFileCount > 0)
        {
            DiagnosticLog.Info(
                "App",
                $"Legacy application data moved to portable storage; files={migration.MigratedFileCount}; " +
                $"directory={ApplicationDataPaths.RootDirectory}");
        }

        foreach (var warning in migration.Warnings)
        {
            DiagnosticLog.Warning("App", warning);
        }

        if (migration.HasBlockingFailure)
        {
            DiagnosticLog.Warning("App", "Startup stopped because legacy application data could not be migrated");
            MessageBox.Show(
                $"旧版任务或设置无法迁移。为避免覆盖原数据，downloader 已停止启动。\n\n请检查数据目录：\n{ApplicationDataPaths.RootDirectory}",
                AppInfo.WindowTitle,
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown(1);
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
