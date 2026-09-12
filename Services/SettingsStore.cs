using System.Text.Json;
using BigFileDownloader.Models;

namespace BigFileDownloader.Services;

internal sealed class SettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly string _filePath;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public SettingsStore(string? filePath = null)
    {
        _filePath = filePath ?? ApplicationDataPaths.SettingsFilePath;
    }

    public async Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default)
    {
        var fallback = AppSettings.CreateDefault(ApplicationDataPaths.DefaultDownloadDirectory);
        if (!File.Exists(_filePath))
        {
            DiagnosticLog.Info("Settings", $"No saved settings found; path={_filePath}");
            return fallback;
        }

        try
        {
            await using var stream = File.OpenRead(_filePath);
            var settings = await JsonSerializer.DeserializeAsync<AppSettings>(stream, JsonOptions, cancellationToken);
            if (settings is null)
            {
                DiagnosticLog.Warning("Settings", $"Saved settings are empty; path={_filePath}");
                return fallback;
            }

            if (settings.SchemaVersion != AppSettings.CurrentSchemaVersion)
            {
                DiagnosticLog.Warning(
                    "Settings",
                    $"Unsupported settings schema; path={_filePath}; schema={settings.SchemaVersion}");
                return fallback;
            }

            if (!TryNormalizeDirectory(settings.DefaultDownloadDirectory, out var directory))
            {
                DiagnosticLog.Warning("Settings", $"Saved default directory is invalid; path={_filePath}");
                return fallback;
            }

            DiagnosticLog.Info("Settings", $"Settings loaded; path={_filePath}");
            return settings with { DefaultDownloadDirectory = directory };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
        {
            DiagnosticLog.Error("Settings", $"Saved settings could not be loaded; path={_filePath}", exception);
            return fallback;
        }
    }

    public async Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (!TryNormalizeDirectory(settings.DefaultDownloadDirectory, out var directory))
        {
            throw new ArgumentException("默认下载位置必须是有效的绝对路径。", nameof(settings));
        }

        var normalized = settings with
        {
            SchemaVersion = AppSettings.CurrentSchemaVersion,
            DefaultDownloadDirectory = directory
        };

        await _gate.WaitAsync(cancellationToken);
        var temporaryPath = _filePath + ".tmp";
        try
        {
            var parentDirectory = Path.GetDirectoryName(_filePath)
                ?? throw new InvalidOperationException("设置文件路径无效。");
            Directory.CreateDirectory(parentDirectory);
            await using (var stream = new FileStream(
                temporaryPath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                16 * 1024,
                FileOptions.Asynchronous))
            {
                await JsonSerializer.SerializeAsync(stream, normalized, JsonOptions, cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }

            File.Move(temporaryPath, _filePath, true);
            DiagnosticLog.Info("Settings", $"Settings saved; path={_filePath}");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            DiagnosticLog.Error("Settings", $"Settings could not be saved; path={_filePath}", exception);
            throw;
        }
        finally
        {
            try
            {
                if (File.Exists(temporaryPath))
                {
                    File.Delete(temporaryPath);
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                DiagnosticLog.Warning("Settings", $"Temporary settings file could not be removed: {exception.Message}");
            }

            _gate.Release();
        }
    }

    private static bool TryNormalizeDirectory(string? value, out string directory)
    {
        directory = string.Empty;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        try
        {
            var candidate = value.Trim();
            if (!Path.IsPathFullyQualified(candidate))
            {
                return false;
            }

            directory = Path.GetFullPath(candidate);
            return true;
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
        {
            return false;
        }
    }
}
