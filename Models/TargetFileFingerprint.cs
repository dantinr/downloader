namespace BigFileDownloader.Models;

public sealed record TargetFileFingerprint(
    string FileIdentity,
    long Length,
    long LastWriteTimeUtcTicks);
