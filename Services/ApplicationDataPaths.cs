using System.Security.Cryptography;
using System.Text.Json;

namespace BigFileDownloader.Services;

internal sealed record StorageMigrationResult(
    int MigratedFileCount,
    IReadOnlyList<string> Warnings,
    bool HasBlockingFailure);

internal static class ApplicationDataPaths
{
    private const string QueueFileName = "queue.json";
    private const string SettingsFileName = "settings.json";

    public static string RootDirectory { get; } = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "data"));

    public static string QueueFilePath { get; } = Path.Combine(RootDirectory, QueueFileName);

    public static string SettingsFilePath { get; } = Path.Combine(RootDirectory, SettingsFileName);

    public static string LogsDirectory { get; } = Path.Combine(RootDirectory, "logs");

    public static string DefaultDownloadDirectory { get; } = Path.Combine(
        AppContext.BaseDirectory,
        "downloads");

    public static StorageMigrationResult MigrateLegacyData(
        string? legacyLocalApplicationData = null,
        string? destinationRootDirectory = null)
    {
        var legacyRoot = Path.GetFullPath(legacyLocalApplicationData
            ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
        var destinationRoot = Path.GetFullPath(destinationRootDirectory ?? RootDirectory);
        var warnings = new List<string>();
        var migratedFileCount = 0;

        var legacyApplicationDirectory = Path.Combine(legacyRoot, "BigFileDownloader");
        var legacyQueuePath = Path.Combine(legacyApplicationDirectory, QueueFileName);
        var destinationQueuePath = Path.Combine(destinationRoot, QueueFileName);
        var queueMigrationSucceeded = TryMigrateFile(
            legacyQueuePath,
            destinationQueuePath,
            ref migratedFileCount,
            warnings,
            preferNonEmptyJsonArray: true);

        var legacySettingsPath = Path.Combine(legacyApplicationDirectory, SettingsFileName);
        var destinationSettingsPath = Path.Combine(destinationRoot, SettingsFileName);
        var settingsMigrationSucceeded = TryMigrateFile(
            legacySettingsPath,
            destinationSettingsPath,
            ref migratedFileCount,
            warnings);

        var legacyLogsDirectory = Path.Combine(legacyRoot, "downloader", "logs");
        try
        {
            if (Directory.Exists(legacyLogsDirectory))
            {
                foreach (var sourcePath in Directory.EnumerateFiles(legacyLogsDirectory, "*.log"))
                {
                    _ = TryMigrateFile(
                        sourcePath,
                        Path.Combine(destinationRoot, "logs", Path.GetFileName(sourcePath)),
                        ref migratedFileCount,
                        warnings);
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            warnings.Add($"旧日志目录无法读取：{exception.Message}");
        }

        TryDeleteEmptyDirectory(legacyLogsDirectory, warnings);
        TryDeleteEmptyDirectory(Path.GetDirectoryName(legacyLogsDirectory), warnings);
        TryDeleteEmptyDirectory(legacyApplicationDirectory, warnings);

        var hasBlockingFailure = !queueMigrationSucceeded || !settingsMigrationSucceeded;
        return new StorageMigrationResult(migratedFileCount, warnings, hasBlockingFailure);
    }

    private static bool TryMigrateFile(
        string sourcePath,
        string preferredDestinationPath,
        ref int migratedFileCount,
        ICollection<string> warnings,
        bool preferNonEmptyJsonArray = false)
    {
        if (Path.GetFullPath(sourcePath).Equals(
            Path.GetFullPath(preferredDestinationPath),
            StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!File.Exists(sourcePath))
        {
            return true;
        }

        string? temporaryPath = null;
        try
        {
            var destinationPath = preferredDestinationPath;
            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)
                ?? throw new InvalidOperationException("应用数据目标目录无效。"));

            if (File.Exists(destinationPath))
            {
                if (FilesMatch(sourcePath, destinationPath))
                {
                    File.Delete(sourcePath);
                    migratedFileCount++;
                    return true;
                }

                if (preferNonEmptyJsonArray && ShouldReplaceDestination(sourcePath, destinationPath))
                {
                    File.Move(destinationPath, BuildLegacyBackupPath(destinationPath), overwrite: false);
                }
                else
                {
                    destinationPath = BuildLegacyBackupPath(destinationPath);
                }
            }

            temporaryPath = destinationPath + $".migrating-{Guid.NewGuid():N}.tmp";
            File.Copy(sourcePath, temporaryPath, overwrite: false);
            if (!FilesMatch(sourcePath, temporaryPath))
            {
                throw new IOException("迁移后的文件校验失败。");
            }

            File.Move(temporaryPath, destinationPath, overwrite: false);
            temporaryPath = null;
            File.Delete(sourcePath);
            migratedFileCount++;
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            warnings.Add($"无法迁移 {sourcePath}：{exception.Message}");
            return false;
        }
        finally
        {
            if (temporaryPath is not null)
            {
                try
                {
                    File.Delete(temporaryPath);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    warnings.Add($"迁移临时文件无法清理：{exception.Message}");
                }
            }
        }
    }

    private static bool FilesMatch(string firstPath, string secondPath)
    {
        var firstInfo = new FileInfo(firstPath);
        var secondInfo = new FileInfo(secondPath);
        if (firstInfo.Length != secondInfo.Length)
        {
            return false;
        }

        using var firstStream = File.OpenRead(firstPath);
        using var secondStream = File.OpenRead(secondPath);
        return SHA256.HashData(firstStream).SequenceEqual(SHA256.HashData(secondStream));
    }

    private static bool ShouldReplaceDestination(string sourcePath, string destinationPath)
    {
        return TryGetJsonArrayLength(sourcePath, out var sourceCount)
            && sourceCount > 0
            && (!TryGetJsonArrayLength(destinationPath, out var destinationCount) || destinationCount == 0);
    }

    private static bool TryGetJsonArrayLength(string path, out int count)
    {
        count = 0;
        try
        {
            using var stream = File.OpenRead(path);
            using var document = JsonDocument.Parse(stream);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                return false;
            }

            count = document.RootElement.GetArrayLength();
            return true;
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static string BuildLegacyBackupPath(string destinationPath)
    {
        var directory = Path.GetDirectoryName(destinationPath)
            ?? throw new InvalidOperationException("应用数据目标目录无效。");
        var stem = Path.GetFileNameWithoutExtension(destinationPath);
        var extension = Path.GetExtension(destinationPath);
        return Path.Combine(
            directory,
            $"{stem}.legacy-{DateTime.UtcNow:yyyyMMddHHmmss}-{Guid.NewGuid():N}{extension}");
    }

    private static void TryDeleteEmptyDirectory(string? path, ICollection<string> warnings)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
        {
            return;
        }

        try
        {
            if (!Directory.EnumerateFileSystemEntries(path).Any())
            {
                Directory.Delete(path);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            warnings.Add($"旧数据空目录无法清理：{exception.Message}");
        }
    }
}
