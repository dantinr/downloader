using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using BigFileDownloader.Models;
using BigFileDownloader.Services;

var root = Path.Combine(Path.GetTempPath(), $"downloader-selftest-{Guid.NewGuid():N}");
Directory.CreateDirectory(root);

try
{
    var data = new byte[4096];
    new Random(20260908).NextBytes(data);
    using var handler = new ExpiringRedirectHandler(data);
    using var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
    using var engine = new DownloadEngine(client, minimumSegmentSize: 1);
    var referrer = "https://www.moddb.com/mods/example/downloads";
    var job = new DownloadJob
    {
        Url = "https://origin.test/mirror?id=short-lived&referer=" + Uri.EscapeDataString(referrer),
        DestinationFolder = root,
        SegmentCount = 2
    };

    await engine.DownloadAsync(job, new InlineProgress(), CancellationToken.None);

    Assert(job.ResolvedTargetPath is not null && File.Exists(job.ResolvedTargetPath),
        "redirected download creates the target file");
    Assert(File.ReadAllBytes(job.ResolvedTargetPath!).SequenceEqual(data),
        "redirected download content matches the source");
    Assert(handler.ExpiredFinalRequests == 0,
        "data requests do not reuse the temporary final URL");
    Assert(handler.Requests.Count == 3 && handler.Requests.All(item => item.Method == HttpMethod.Get),
        "probe and two segments use only GET requests");
    Assert(handler.Requests.All(item => item.Referrer == referrer),
        "probe and every segment preserve the source Referer");

    Console.WriteLine("PASS: short-lived redirect authorization is refreshed per request.");
}
finally
{
    Directory.Delete(root, true);
}

static void Assert(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException("FAIL: " + message);
    }
}

internal sealed class InlineProgress : IProgress<DownloadProgress>
{
    public void Report(DownloadProgress value)
    {
    }
}

internal sealed class ExpiringRedirectHandler(byte[] data) : HttpMessageHandler
{
    private readonly byte[] _data = data;
    private int _expiredFinalRequests;

    public ConcurrentQueue<CapturedRequest> Requests { get; } = new();

    public int ExpiredFinalRequests => Volatile.Read(ref _expiredFinalRequests);

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        if (request.RequestUri?.Host == "temporary-cdn.test")
        {
            Interlocked.Increment(ref _expiredFinalRequests);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden));
        }

        if (request.RequestUri?.Host != "origin.test"
            || request.Headers.Range?.Ranges.SingleOrDefault() is not { } range
            || range.From is null)
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest));
        }

        var start = range.From.Value;
        var end = range.To ?? (_data.LongLength - 1);
        var referrer = request.Headers.Referrer?.AbsoluteUri;
        Requests.Enqueue(new CapturedRequest(request.Method, referrer));

        var response = new HttpResponseMessage(HttpStatusCode.PartialContent)
        {
            Content = new ByteArrayContent(_data.AsMemory((int)start, (int)(end - start + 1)).ToArray()),
            RequestMessage = new HttpRequestMessage(
                request.Method,
                $"https://temporary-cdn.test/file?expires=1&token=secret")
        };
        response.Headers.ETag = new EntityTagHeaderValue("\"redirect-v1\"");
        response.Content.Headers.ContentRange = new ContentRangeHeaderValue(start, end, _data.LongLength);
        response.Content.Headers.ContentDisposition = new ContentDispositionHeaderValue("attachment")
        {
            FileNameStar = "redirect.bin"
        };
        return Task.FromResult(response);
    }
}

internal sealed record CapturedRequest(HttpMethod Method, string? Referrer);
