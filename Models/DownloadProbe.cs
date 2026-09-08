namespace BigFileDownloader.Models;

internal sealed record DownloadProbe(
    Uri FinalUri,
    string FileName,
    long? TotalBytes,
    bool SupportsRanges,
    string? ETag,
    DateTimeOffset? LastModified);
