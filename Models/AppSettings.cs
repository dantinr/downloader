namespace BigFileDownloader.Models;

internal sealed record AppSettings
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    public string DefaultDownloadDirectory { get; init; } = string.Empty;

    public static AppSettings CreateDefault(string directory) => new()
    {
        DefaultDownloadDirectory = directory
    };
}
