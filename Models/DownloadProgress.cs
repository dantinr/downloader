namespace BigFileDownloader.Models;

public readonly record struct DownloadProgress(long DownloadedBytes, long? TotalBytes, double BytesPerSecond);
