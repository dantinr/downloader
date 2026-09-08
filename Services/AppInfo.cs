namespace BigFileDownloader.Services;

internal static class AppInfo
{
    public const string ProductName = "downloader";
    public const string Description = "可靠下载与断点续传";
    public const string LicenseName = "Apache License 2.0";
    public const string RepositoryUrl = "https://github.com/dantinr/downloader";
    public const string LicenseUrl = "https://github.com/dantinr/downloader/blob/main/LICENSE";

    public static string Version { get; } =
        typeof(AppInfo).Assembly.GetName().Version?.ToString(3) ?? "unknown";

    public static string WindowTitle => $"{ProductName} {Version}";
}
