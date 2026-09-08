using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Web;
using BigFileDownloader.Models;

namespace BigFileDownloader.Services;

internal sealed class DownloadEngine : IDisposable
{
    private const int BufferSize = 256 * 1024;
    private const int MaximumAttempts = 4;
    private readonly HttpClient _client;
    private readonly bool _ownsClient;
    private readonly long _minimumSegmentSize;

    public DownloadEngine(HttpClient? client = null, long minimumSegmentSize = 8 * 1024 * 1024)
    {
        _minimumSegmentSize = minimumSegmentSize;
        if (client is not null)
        {
            _client = client;
            return;
        }

        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = true,
            AutomaticDecompression = DecompressionMethods.None,
            ConnectTimeout = TimeSpan.FromSeconds(20),
            MaxConnectionsPerServer = 32,
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
            UseProxy = true
        };
        _client = new HttpClient(handler)
        {
            Timeout = Timeout.InfiniteTimeSpan
        };
        _ownsClient = true;
    }

    public async Task<DownloadProbe> ProbeAsync(string url, CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
        {
            throw new ArgumentException("请输入有效的 HTTP 或 HTTPS 下载地址。", nameof(url));
        }

        DiagnosticLog.Info("Engine", $"Probe started; url={DiagnosticLog.SafeUrl(url)}");
        using var rangeRequest = CreateRequest(HttpMethod.Get, uri);
        rangeRequest.Headers.Range = new RangeHeaderValue(0, 0);
        using var rangeResponse = await _client.SendAsync(
            rangeRequest,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);

        if (!rangeResponse.IsSuccessStatusCode)
        {
            DiagnosticLog.Warning(
                "Engine",
                $"Range probe rejected; url={DiagnosticLog.SafeUrl(url)}; status={(int)rangeResponse.StatusCode}");
            throw new HttpRequestException(
                $"服务器拒绝下载请求：{(int)rangeResponse.StatusCode} {rangeResponse.ReasonPhrase}",
                null,
                rangeResponse.StatusCode);
        }

        var supportsRanges = rangeResponse.StatusCode == HttpStatusCode.PartialContent;
        var totalBytes = rangeResponse.Content.Headers.ContentRange?.Length
            ?? rangeResponse.Content.Headers.ContentLength;
        var etag = rangeResponse.Headers.ETag?.ToString();
        var lastModified = rangeResponse.Content.Headers.LastModified;
        var disposition = rangeResponse.Content.Headers.ContentDisposition;
        var finalUri = rangeResponse.RequestMessage?.RequestUri ?? uri;

        DiagnosticLog.Info(
            "Engine",
            $"Probe completed; source={DiagnosticLog.SafeUrl(url)}; final={DiagnosticLog.SafeUrl(finalUri.AbsoluteUri)}; " +
            $"status={(int)rangeResponse.StatusCode}; bytes={totalBytes?.ToString() ?? "unknown"}; ranges={supportsRanges}");

        return new DownloadProbe(
            finalUri,
            FileNameHelper.FromResponse(disposition, finalUri),
            totalBytes,
            supportsRanges,
            etag,
            lastModified);
    }

    public async Task DownloadAsync(
        DownloadJob job,
        IProgress<DownloadProgress> progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(progress);

        DiagnosticLog.Info(
            "Engine",
            $"Download started; job={job.Id:N}; url={DiagnosticLog.SafeUrl(job.Url)}; " +
            $"requestedSegments={job.SegmentCount}; existingBytes={job.DownloadedBytes}");
        job.State = DownloadState.Inspecting;
        job.Message = "正在检查服务器";
        var previousTotalBytes = job.TotalBytes;
        var previousEtag = job.ETag;
        var previousLastModified = job.LastModified;
        var hadPartialData = HasPartialData(job);
        var sourceUri = new Uri(job.Url, UriKind.Absolute);
        var probe = await ProbeAsync(job.Url, cancellationToken);

        if (hadPartialData && RemoteFileChanged(previousTotalBytes, previousEtag, previousLastModified, probe))
        {
            DiagnosticLog.Warning("Engine", $"Remote file changed; job={job.Id:N}");
            throw new InvalidOperationException("远端文件已经变化。请删除旧任务后重新添加，避免文件损坏。");
        }

        Directory.CreateDirectory(job.DestinationFolder);
        if (string.IsNullOrWhiteSpace(job.ResolvedTargetPath))
        {
            job.ResolvedTargetPath = FileNameHelper.EnsureUniquePath(job.DestinationFolder, probe.FileName);
        }

        job.FileName = Path.GetFileName(job.ResolvedTargetPath);
        job.TotalBytes = probe.TotalBytes;
        job.SupportsRanges = probe.SupportsRanges;
        job.ETag = probe.ETag;
        job.LastModified = probe.LastModified;

        if (File.Exists(job.ResolvedTargetPath)
            && probe.TotalBytes is > 0
            && new FileInfo(job.ResolvedTargetPath).Length == probe.TotalBytes.Value)
        {
            job.DownloadedBytes = probe.TotalBytes.Value;
            progress.Report(new DownloadProgress(probe.TotalBytes.Value, probe.TotalBytes, 0));
            DiagnosticLog.Info("Engine", $"Existing target is already complete; job={job.Id:N}");
            return;
        }

        if (probe.TotalBytes == 0)
        {
            await File.WriteAllBytesAsync(job.ResolvedTargetPath, [], cancellationToken);
            progress.Report(new DownloadProgress(0, 0, 0));
            DiagnosticLog.Info("Engine", $"Created empty target; job={job.Id:N}; file={job.ResolvedTargetPath}");
            return;
        }

        if (probe.SupportsRanges && probe.TotalBytes is > 0)
        {
            job.Message = $"{job.SegmentCount} 路连接，支持断点续传";
            DiagnosticLog.Info("Engine", $"Using segmented mode; job={job.Id:N}; bytes={probe.TotalBytes}");
            await DownloadSegmentedAsync(job, sourceUri, progress, cancellationToken);
        }
        else
        {
            job.Message = "服务器不支持 Range，使用单连接下载";
            DiagnosticLog.Info("Engine", $"Using single-stream mode; job={job.Id:N}; bytes={probe.TotalBytes}");
            await DownloadSingleAsync(job, sourceUri, progress, cancellationToken);
        }
    }

    public void DeletePartialData(DownloadJob job)
    {
        if (string.IsNullOrWhiteSpace(job.ResolvedTargetPath))
        {
            return;
        }

        var partsDirectory = PartsDirectory(job.ResolvedTargetPath);
        if (Directory.Exists(partsDirectory))
        {
            Directory.Delete(partsDirectory, true);
        }

        DeleteIfExists(TemporaryPath(job.ResolvedTargetPath));
        DeleteIfExists(AssemblingPath(job.ResolvedTargetPath));
        DiagnosticLog.Info("Engine", $"Partial data deleted; job={job.Id:N}; target={job.ResolvedTargetPath}");
    }

    private async Task DownloadSegmentedAsync(
        DownloadJob job,
        Uri uri,
        IProgress<DownloadProgress> progress,
        CancellationToken cancellationToken)
    {
        var totalBytes = job.TotalBytes!.Value;
        var ranges = RangePlanner.Plan(totalBytes, job.SegmentCount, _minimumSegmentSize);
        job.SegmentCount = ranges.Count;
        var partsDirectory = PartsDirectory(job.ResolvedTargetPath!);
        Directory.CreateDirectory(partsDirectory);

        long downloadedBytes = 0;
        foreach (var range in ranges)
        {
            var partPath = PartPath(partsDirectory, range.Index);
            if (!File.Exists(partPath))
            {
                continue;
            }

            var currentLength = new FileInfo(partPath).Length;
            if (currentLength > range.Length)
            {
                using var stream = new FileStream(partPath, FileMode.Open, FileAccess.Write, FileShare.None);
                stream.SetLength(0);
                continue;
            }

            downloadedBytes += currentLength;
        }

        DiagnosticLog.Info(
            "Engine",
            $"Segments planned; job={job.Id:N}; segments={ranges.Count}; bytes={totalBytes}; recoveredBytes={downloadedBytes}");
        job.State = DownloadState.Downloading;
        progress.Report(new DownloadProgress(downloadedBytes, totalBytes, 0));

        var tasks = ranges.Select(range => DownloadSegmentWithRetriesAsync(
            job,
            uri,
            partsDirectory,
            range,
            bytesRead => Interlocked.Add(ref downloadedBytes, bytesRead),
            cancellationToken)).ToArray();

        var transfer = Task.WhenAll(tasks);
        var reporter = ReportProgressUntilCompleteAsync(
            transfer,
            () => Interlocked.Read(ref downloadedBytes),
            totalBytes,
            progress);

        try
        {
            await transfer;
        }
        finally
        {
            await reporter;
        }

        cancellationToken.ThrowIfCancellationRequested();
        progress.Report(new DownloadProgress(totalBytes, totalBytes, 0));
        job.State = DownloadState.Merging;
        job.Message = "正在校验并合并分块";
        DiagnosticLog.Info("Engine", $"All segments complete; merging; job={job.Id:N}");
        await MergePartsAsync(job, ranges, partsDirectory, cancellationToken);
        DiagnosticLog.Info("Engine", $"Segmented download finalized; job={job.Id:N}; target={job.ResolvedTargetPath}");
    }

    private async Task DownloadSegmentWithRetriesAsync(
        DownloadJob job,
        Uri uri,
        string partsDirectory,
        SegmentRange range,
        Action<int> reportBytes,
        CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await DownloadSegmentOnceAsync(job, uri, partsDirectory, range, reportBytes, cancellationToken);
                return;
            }
            catch (Exception exception) when (
                attempt < MaximumAttempts
                && !cancellationToken.IsCancellationRequested
                && exception is HttpRequestException or IOException)
            {
                var delay = TimeSpan.FromSeconds(Math.Pow(2, attempt - 1));
                DiagnosticLog.Warning(
                    "Engine",
                    $"Segment retry scheduled; job={job.Id:N}; segment={range.Index + 1}; " +
                    $"attempt={attempt}; delaySeconds={delay.TotalSeconds:0}; " +
                    $"error={exception.GetType().Name}: {exception.Message}");
                await Task.Delay(delay, cancellationToken);
            }
        }
    }

    private async Task DownloadSegmentOnceAsync(
        DownloadJob job,
        Uri uri,
        string partsDirectory,
        SegmentRange range,
        Action<int> reportBytes,
        CancellationToken cancellationToken)
    {
        var partPath = PartPath(partsDirectory, range.Index);
        var existingLength = File.Exists(partPath) ? new FileInfo(partPath).Length : 0;
        if (existingLength == range.Length)
        {
            return;
        }

        var requestStart = range.Start + existingLength;
        DiagnosticLog.Info(
            "Engine",
            $"Segment request; job={job.Id:N}; segment={range.Index + 1}/{job.SegmentCount}; " +
            $"start={requestStart}; end={range.End}; resumedBytes={existingLength}");
        using var request = CreateRequest(HttpMethod.Get, uri);
        request.Headers.Range = new RangeHeaderValue(requestStart, range.End);
        AddIfRangeHeader(request, job);

        using var response = await _client.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);

        if (response.StatusCode != HttpStatusCode.PartialContent)
        {
            throw new HttpRequestException(
                "服务器没有按 Range 返回分块，下载已停止以避免文件损坏。",
                null,
                response.StatusCode);
        }

        var contentRange = response.Content.Headers.ContentRange;
        if (contentRange?.From != requestStart || contentRange.To != range.End)
        {
            throw new HttpRequestException("服务器返回的分块范围与请求不一致。");
        }

        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var destination = new FileStream(
            partPath,
            FileMode.OpenOrCreate,
            FileAccess.Write,
            FileShare.Read,
            BufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        destination.Position = existingLength;

        var buffer = new byte[BufferSize];
        while (true)
        {
            var read = await source.ReadAsync(buffer, cancellationToken);
            if (read == 0)
            {
                break;
            }

            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            reportBytes(read);
        }

        await destination.FlushAsync(cancellationToken);
        if (destination.Length != range.Length)
        {
            throw new IOException($"分块 {range.Index + 1} 长度不完整。");
        }

        DiagnosticLog.Info(
            "Engine",
            $"Segment completed; job={job.Id:N}; segment={range.Index + 1}/{job.SegmentCount}; bytes={range.Length}");
    }

    private async Task DownloadSingleAsync(
        DownloadJob job,
        Uri uri,
        IProgress<DownloadProgress> progress,
        CancellationToken cancellationToken)
    {
        var targetPath = job.ResolvedTargetPath!;
        var temporaryPath = TemporaryPath(targetPath);
        DeleteIfExists(temporaryPath);
        long downloadedBytes = 0;
        job.DownloadedBytes = 0;
        job.State = DownloadState.Downloading;
        DiagnosticLog.Info("Engine", $"Single stream opened; job={job.Id:N}; target={targetPath}");

        var transfer = DownloadSingleStreamAsync(
            uri,
            temporaryPath,
            bytesRead => Interlocked.Add(ref downloadedBytes, bytesRead),
            cancellationToken);
        var reporter = ReportProgressUntilCompleteAsync(
            transfer,
            () => Interlocked.Read(ref downloadedBytes),
            job.TotalBytes,
            progress);

        try
        {
            await transfer;
        }
        finally
        {
            await reporter;
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (File.Exists(targetPath))
        {
            throw new IOException("目标文件已存在，下载结果保留在临时文件中。");
        }

        File.Move(temporaryPath, targetPath);
        progress.Report(new DownloadProgress(downloadedBytes, job.TotalBytes ?? downloadedBytes, 0));
        DiagnosticLog.Info("Engine", $"Single-stream download finalized; job={job.Id:N}; bytes={downloadedBytes}; target={targetPath}");
    }

    private async Task DownloadSingleStreamAsync(
        Uri uri,
        string temporaryPath,
        Action<int> reportBytes,
        CancellationToken cancellationToken)
    {
        using var request = CreateRequest(HttpMethod.Get, uri);
        using var response = await _client.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        response.EnsureSuccessStatusCode();

        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var destination = new FileStream(
            temporaryPath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.Read,
            BufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

        var buffer = new byte[BufferSize];
        while (true)
        {
            var read = await source.ReadAsync(buffer, cancellationToken);
            if (read == 0)
            {
                break;
            }

            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            reportBytes(read);
        }

        await destination.FlushAsync(cancellationToken);
    }

    private static async Task MergePartsAsync(
        DownloadJob job,
        IReadOnlyList<SegmentRange> ranges,
        string partsDirectory,
        CancellationToken cancellationToken)
    {
        var targetPath = job.ResolvedTargetPath!;
        var assemblingPath = AssemblingPath(targetPath);
        DiagnosticLog.Info("Engine", $"Merge started; job={job.Id:N}; segments={ranges.Count}; target={targetPath}");
        DeleteIfExists(assemblingPath);

        await using (var destination = new FileStream(
            assemblingPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            BufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan))
        {
            foreach (var range in ranges)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var partPath = PartPath(partsDirectory, range.Index);
                var info = new FileInfo(partPath);
                if (!info.Exists || info.Length != range.Length)
                {
                    throw new IOException($"分块 {range.Index + 1} 不完整，无法合并。");
                }

                await using var source = new FileStream(
                    partPath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    BufferSize,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
                await source.CopyToAsync(destination, BufferSize, cancellationToken);
            }

            await destination.FlushAsync(cancellationToken);
        }

        if (job.TotalBytes is > 0 && new FileInfo(assemblingPath).Length != job.TotalBytes.Value)
        {
            throw new IOException("合并后的文件长度与服务器报告不一致。");
        }

        if (File.Exists(targetPath))
        {
            throw new IOException("目标文件已存在，合并结果已保留。");
        }

        File.Move(assemblingPath, targetPath);
        Directory.Delete(partsDirectory, true);
        DiagnosticLog.Info("Engine", $"Merge completed; job={job.Id:N}; target={targetPath}");
    }

    private static async Task ReportProgressUntilCompleteAsync(
        Task transfer,
        Func<long> readDownloadedBytes,
        long? totalBytes,
        IProgress<DownloadProgress> progress)
    {
        var stopwatch = Stopwatch.StartNew();
        var lastTimestamp = stopwatch.Elapsed;
        var lastBytes = readDownloadedBytes();
        var smoothedSpeed = 0d;

        while (!transfer.IsCompleted)
        {
            await Task.WhenAny(transfer, Task.Delay(350));
            var timestamp = stopwatch.Elapsed;
            var currentBytes = readDownloadedBytes();
            var elapsed = (timestamp - lastTimestamp).TotalSeconds;
            if (elapsed <= 0)
            {
                continue;
            }

            var instantSpeed = Math.Max(0, currentBytes - lastBytes) / elapsed;
            smoothedSpeed = smoothedSpeed <= 0 ? instantSpeed : (smoothedSpeed * 0.62) + (instantSpeed * 0.38);
            progress.Report(new DownloadProgress(currentBytes, totalBytes, smoothedSpeed));
            lastTimestamp = timestamp;
            lastBytes = currentBytes;
        }

        progress.Report(new DownloadProgress(readDownloadedBytes(), totalBytes, 0));
    }

    private static HttpRequestMessage CreateRequest(HttpMethod method, Uri uri)
    {
        var request = new HttpRequestMessage(method, uri);
        request.Headers.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) " +
            "AppleWebKit/537.36 (KHTML, like Gecko) " +
            "Chrome/140.0.0.0 Safari/537.36 downloader/1.0");
        request.Headers.Accept.ParseAdd("*/*");
        var referrer = HttpUtility.ParseQueryString(uri.Query)["referer"];
        if (Uri.TryCreate(referrer, UriKind.Absolute, out var referrerUri)
            && referrerUri.Scheme is "http" or "https")
        {
            request.Headers.Referrer = referrerUri;
        }

        return request;
    }

    private static void AddIfRangeHeader(HttpRequestMessage request, DownloadJob job)
    {
        if (!string.IsNullOrWhiteSpace(job.ETag)
            && EntityTagHeaderValue.TryParse(job.ETag, out var entityTag))
        {
            request.Headers.IfRange = new RangeConditionHeaderValue(entityTag);
        }
        else if (job.LastModified is not null)
        {
            request.Headers.IfRange = new RangeConditionHeaderValue(job.LastModified.Value);
        }
    }

    private static bool RemoteFileChanged(
        long? previousTotalBytes,
        string? previousEtag,
        DateTimeOffset? previousLastModified,
        DownloadProbe probe)
    {
        if (!string.IsNullOrWhiteSpace(previousEtag)
            && !string.IsNullOrWhiteSpace(probe.ETag))
        {
            return !string.Equals(previousEtag, probe.ETag, StringComparison.Ordinal);
        }

        if (previousLastModified is not null
            && probe.LastModified is not null
            && previousLastModified.Value != probe.LastModified.Value)
        {
            return true;
        }

        return previousTotalBytes is not null
            && probe.TotalBytes is not null
            && previousTotalBytes.Value != probe.TotalBytes.Value;
    }

    private static bool HasPartialData(DownloadJob job)
    {
        if (string.IsNullOrWhiteSpace(job.ResolvedTargetPath))
        {
            return false;
        }

        return Directory.Exists(PartsDirectory(job.ResolvedTargetPath))
            || File.Exists(TemporaryPath(job.ResolvedTargetPath))
            || File.Exists(AssemblingPath(job.ResolvedTargetPath));
    }

    private static string PartsDirectory(string targetPath) => targetPath + ".bfdl.parts";

    private static string PartPath(string partsDirectory, int index) =>
        Path.Combine(partsDirectory, $"part-{index:D3}.tmp");

    private static string TemporaryPath(string targetPath) => targetPath + ".bfdl.tmp";

    private static string AssemblingPath(string targetPath) => targetPath + ".assembling";

    private static void DeleteIfExists(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    public void Dispose()
    {
        if (_ownsClient)
        {
            _client.Dispose();
        }
    }
}
