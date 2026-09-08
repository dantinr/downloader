namespace BigFileDownloader.Models;

public enum DownloadState
{
    Pending,
    Inspecting,
    Downloading,
    Pausing,
    Paused,
    Merging,
    Completed,
    Failed
}
