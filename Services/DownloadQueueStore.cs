using System.Text.Json;
using System.Text.Json.Serialization;
using BigFileDownloader.Models;

namespace BigFileDownloader.Services;

internal sealed class DownloadQueueStore
{
    private readonly string _filePath;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() }
    };

    public DownloadQueueStore(string? filePath = null)
    {
        _filePath = filePath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "BigFileDownloader",
            "queue.json");
    }

    public async Task<IReadOnlyList<DownloadJob>> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_filePath))
        {
            DiagnosticLog.Info("Queue", $"No saved queue found; path={_filePath}");
            return [];
        }

        try
        {
            await using var stream = File.OpenRead(_filePath);
            var jobs = await JsonSerializer.DeserializeAsync<List<DownloadJob>>(stream, JsonOptions, cancellationToken);
            foreach (var job in jobs ?? [])
            {
                job.Cancellation = null;
                job.ActiveTask = null;
                job.BytesPerSecond = 0;
                if (job.State is DownloadState.Downloading or DownloadState.Inspecting or DownloadState.Merging or DownloadState.Pausing)
                {
                    job.State = DownloadState.Paused;
                    job.Message = "上次退出后已暂停";
                }
            }

            var restored = jobs ?? [];
            DiagnosticLog.Info("Queue", $"Queue loaded; path={_filePath}; jobs={restored.Count}");
            return restored;
        }
        catch (JsonException exception)
        {
            DiagnosticLog.Error("Queue", $"Saved queue is invalid JSON; path={_filePath}", exception);
            return [];
        }
        catch (IOException exception)
        {
            DiagnosticLog.Error("Queue", $"Saved queue could not be read; path={_filePath}", exception);
            return [];
        }
    }

    public async Task SaveAsync(IEnumerable<DownloadJob> jobs, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var directory = Path.GetDirectoryName(_filePath)
                ?? throw new InvalidOperationException("任务存储路径无效。");
            Directory.CreateDirectory(directory);
            var temporaryPath = _filePath + ".tmp";

            await using (var stream = new FileStream(
                temporaryPath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                64 * 1024,
                FileOptions.Asynchronous))
            {
                await JsonSerializer.SerializeAsync(stream, jobs.ToList(), JsonOptions, cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }

            File.Move(temporaryPath, _filePath, true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            DiagnosticLog.Error("Queue", $"Queue save failed; path={_filePath}", exception);
            throw;
        }
        finally
        {
            _gate.Release();
        }
    }
}
