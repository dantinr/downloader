using System.Runtime.InteropServices;

namespace BigFileDownloader.Services;

internal static class KnownFolders
{
    private static readonly Guid DownloadsFolderId = new("374DE290-123F-4565-9164-39C4925E467B");

    public static string DownloadsDirectory { get; } = ResolveDownloadsDirectory();

    private static string ResolveDownloadsDirectory()
    {
        nint pathPointer = 0;
        try
        {
            var folderId = DownloadsFolderId;
            var result = SHGetKnownFolderPath(ref folderId, 0, 0, out pathPointer);
            if (result >= 0 && pathPointer != 0 && Marshal.PtrToStringUni(pathPointer) is { Length: > 0 } path)
            {
                return path;
            }
        }
        catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException)
        {
            DiagnosticLog.Warning("Settings", $"Windows Downloads known folder is unavailable: {exception.Message}");
        }
        finally
        {
            if (pathPointer != 0)
            {
                Marshal.FreeCoTaskMem(pathPointer);
            }
        }

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Downloads");
    }

    [DllImport("shell32.dll", ExactSpelling = true)]
    private static extern int SHGetKnownFolderPath(
        ref Guid folderId,
        uint flags,
        nint token,
        out nint path);
}
