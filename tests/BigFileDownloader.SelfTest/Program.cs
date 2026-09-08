using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using BigFileDownloader.Models;
using BigFileDownloader.Services;
using BigFileDownloader.Views;

var root = Path.Combine(Path.GetTempPath(), $"downloader-selftest-{Guid.NewGuid():N}");
Directory.CreateDirectory(root);

try
{
    await TestExpiringRedirectAsync(root);
    TestRemoteIdentityComparison();
    await TestPartialMergeAsync(root);
    await TestSettingsStoreAsync(root);
    TestAppInfo();
    TestUiRendering(root, Path.Combine(Environment.CurrentDirectory, "artifacts", "ui-review"));
    Console.WriteLine("PASS: all self-tests completed.");
}
finally
{
    Directory.Delete(root, true);
}

static async Task TestExpiringRedirectAsync(string root)
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

    Console.WriteLine("  ok: short-lived redirect authorization is refreshed per request");
}

static async Task TestPartialMergeAsync(string root)
{
    var data = new byte[1000];
    new Random(14092026).NextBytes(data);
    var ranges = RangePlanner.Plan(data.LongLength, 2, 1);
    var target = NewPartialJob(Path.Combine(root, "merge-target.bin"), data.LongLength);
    var donor = NewPartialJob(Path.Combine(root, "merge-donor.bin"), data.LongLength);
    var targetParts = target.ResolvedTargetPath + ".bfdl.parts";
    var donorParts = donor.ResolvedTargetPath + ".bfdl.parts";
    Directory.CreateDirectory(targetParts);
    Directory.CreateDirectory(donorParts);
    await File.WriteAllBytesAsync(
        Path.Combine(targetParts, "part-000.tmp"),
        data.AsMemory((int)ranges[0].Start, 300).ToArray());
    await File.WriteAllBytesAsync(
        Path.Combine(targetParts, "part-001.tmp"),
        data.AsMemory((int)ranges[1].Start, (int)ranges[1].Length).ToArray());
    await File.WriteAllBytesAsync(
        Path.Combine(donorParts, "part-000.tmp"),
        data.AsMemory((int)ranges[0].Start, 200).ToArray());

    var service = new PartialDownloadMergeService();
    var targetFirstPartBefore = await File.ReadAllBytesAsync(Path.Combine(targetParts, "part-000.tmp"));
    var targetSecondPartBefore = await File.ReadAllBytesAsync(Path.Combine(targetParts, "part-001.tmp"));
    var result = await service.ValidateRedundantTaskAsync(target, donor);

    Assert(result.RecoveredBytes == 800, "consolidation reports the retained recoverable bytes");
    Assert(result.ComparedBytes == 200, "consolidation compares every redundant byte");
    Assert(File.ReadAllBytes(Path.Combine(targetParts, "part-000.tmp"))
        .SequenceEqual(targetFirstPartBefore), "validation does not rewrite the first target part");
    Assert(File.ReadAllBytes(Path.Combine(targetParts, "part-001.tmp"))
        .SequenceEqual(targetSecondPartBefore), "validation does not rewrite the second target part");
    Assert(Directory.Exists(donorParts), "validation keeps redundant data until the queue is saved");

    var mismatchTarget = NewPartialJob(Path.Combine(root, "mismatch-target.bin"), data.LongLength);
    var mismatchDonor = NewPartialJob(Path.Combine(root, "mismatch-donor.bin"), data.LongLength);
    var mismatchTargetParts = mismatchTarget.ResolvedTargetPath + ".bfdl.parts";
    var mismatchDonorParts = mismatchDonor.ResolvedTargetPath + ".bfdl.parts";
    Directory.CreateDirectory(mismatchTargetParts);
    Directory.CreateDirectory(mismatchDonorParts);
    await File.WriteAllBytesAsync(Path.Combine(mismatchTargetParts, "part-000.tmp"), data.AsSpan(0, 200).ToArray());
    var conflicting = data.AsSpan(0, 200).ToArray();
    conflicting[50] ^= 0xFF;
    await File.WriteAllBytesAsync(Path.Combine(mismatchDonorParts, "part-000.tmp"), conflicting);

    await AssertThrowsAsync<InvalidOperationException>(
        () => service.ValidateRedundantTaskAsync(mismatchTarget, mismatchDonor),
        "consolidation rejects conflicting bytes");
    Assert(File.ReadAllBytes(Path.Combine(mismatchTargetParts, "part-000.tmp"))
        .SequenceEqual(data.AsSpan(0, 200).ToArray()), "failed validation leaves the target prefix unchanged");

    var complementaryTarget = NewPartialJob(Path.Combine(root, "complementary-target.bin"), data.LongLength);
    var complementaryDonor = NewPartialJob(Path.Combine(root, "complementary-donor.bin"), data.LongLength);
    var complementaryTargetParts = complementaryTarget.ResolvedTargetPath + ".bfdl.parts";
    var complementaryDonorParts = complementaryDonor.ResolvedTargetPath + ".bfdl.parts";
    Directory.CreateDirectory(complementaryTargetParts);
    Directory.CreateDirectory(complementaryDonorParts);
    await File.WriteAllBytesAsync(
        Path.Combine(complementaryTargetParts, "part-000.tmp"),
        data.AsSpan(0, 300).ToArray());
    await File.WriteAllBytesAsync(
        Path.Combine(complementaryDonorParts, "part-000.tmp"),
        data.AsSpan(0, 200).ToArray());
    await File.WriteAllBytesAsync(
        Path.Combine(complementaryDonorParts, "part-001.tmp"),
        data.AsSpan((int)ranges[1].Start, 100).ToArray());

    await AssertThrowsAsync<InvalidOperationException>(
        () => service.ValidateRedundantTaskAsync(complementaryTarget, complementaryDonor),
        "consolidation rejects unverified complementary progress");
    Assert(!File.Exists(Path.Combine(complementaryTargetParts, "part-001.tmp")),
        "rejected complementary data is never copied into the retained task");

    Console.WriteLine("  ok: duplicate tasks consolidate without mixing unverified data");
}

static void TestRemoteIdentityComparison()
{
    var modified = new DateTimeOffset(2026, 9, 8, 12, 0, 0, TimeSpan.Zero);
    var matching = new DownloadProbe(
        new Uri("https://mirror.test/file.bin"),
        "file.bin",
        1000,
        true,
        "\"file-v1\"",
        modified);
    var changedEtag = matching with { ETag = "\"file-v2\"" };
    var changedTime = matching with { ETag = null, LastModified = modified.AddMinutes(1) };

    Assert(!DownloadEngine.RemoteFileChanged(1000, "\"file-v1\"", modified, matching),
        "matching remote validators are accepted");
    Assert(DownloadEngine.RemoteFileChanged(1000, "\"file-v1\"", modified, changedEtag),
        "a changed ETag is rejected before a link is transferred");
    Assert(DownloadEngine.RemoteFileChanged(1000, null, modified, changedTime),
        "a changed Last-Modified value is rejected when no ETag is available");
    Assert(DownloadEngine.RemoteFileChanged(1001, "\"file-v1\"", modified, matching),
        "a changed file size is rejected even when the ETag is reused");

    Console.WriteLine("  ok: refreshed links retain their original content identity");
}

static async Task TestSettingsStoreAsync(string root)
{
    var settingsPath = Path.Combine(root, "settings", "settings.json");
    var store = new SettingsStore(settingsPath);
    var defaults = await store.LoadAsync();
    Assert(defaults.SchemaVersion == AppSettings.CurrentSchemaVersion,
        "missing settings use the current schema");
    Assert(Path.IsPathFullyQualified(defaults.DefaultDownloadDirectory),
        "missing settings use an absolute Downloads directory");

    var configuredDirectory = Path.Combine(root, "configured-downloads");
    await store.SaveAsync(new AppSettings { DefaultDownloadDirectory = configuredDirectory });
    var restored = await store.LoadAsync();
    Assert(restored.DefaultDownloadDirectory == Path.GetFullPath(configuredDirectory),
        "the default download directory survives a settings round trip");

    await AssertThrowsAsync<ArgumentException>(
        () => store.SaveAsync(new AppSettings { DefaultDownloadDirectory = "relative\\downloads" }),
        "relative default download directories are rejected");

    const string futureSettings =
        "{\"schemaVersion\":99,\"defaultDownloadDirectory\":\"C:\\\\future-downloads\"}";
    await File.WriteAllTextAsync(settingsPath, futureSettings);
    var futureFallback = await store.LoadAsync();
    Assert(futureFallback.DefaultDownloadDirectory == KnownFolders.DownloadsDirectory,
        "an unknown settings schema falls back to Downloads");
    Assert(await File.ReadAllTextAsync(settingsPath) == futureSettings,
        "an unknown settings schema is not overwritten");

    const string invalidSettings = "{not-valid-json";
    await File.WriteAllTextAsync(settingsPath, invalidSettings);
    var invalidFallback = await store.LoadAsync();
    Assert(invalidFallback.DefaultDownloadDirectory == KnownFolders.DownloadsDirectory,
        "invalid settings fall back to Downloads");
    Assert(await File.ReadAllTextAsync(settingsPath) == invalidSettings,
        "invalid settings are preserved for diagnostics");

    Console.WriteLine("  ok: default download settings persist and fail safely");
}

static void TestAppInfo()
{
    Assert(AppInfo.Version.Split('.').Length == 3 && !AppInfo.Version.Contains('+'),
        "the displayed version is a three-part assembly version without build metadata");
    Assert(AppInfo.WindowTitle == $"downloader {AppInfo.Version}",
        "the main window title includes the application version");

    Console.WriteLine("  ok: application title and about data use the packaged version");
}

static void TestUiRendering(string testRoot, string outputDirectory)
{
    Exception? failure = null;
    var thread = new Thread(() =>
    {
        try
        {
            var application = new BigFileDownloader.App();
            application.InitializeComponent();
            application.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            Directory.CreateDirectory(outputDirectory);

            var mainWindow = new BigFileDownloader.MainWindow();
            mainWindow.Jobs.Add(new DownloadJob
            {
                FileName = "existing-download.bin",
                DestinationFolder = KnownFolders.DownloadsDirectory,
                TotalBytes = 1_000,
                DownloadedBytes = 500,
                State = DownloadState.Paused,
                Message = "已保留分块，可继续下载"
            });
            RenderWindow(mainWindow, Path.Combine(outputDirectory, "main-with-existing-task.png"));
            var progressBar = FindVisualDescendant<ProgressBar>(mainWindow.QueueGrid)
                ?? throw new InvalidOperationException("FAIL: the download progress bar was not rendered");
            var progressBinding = BindingOperations.GetBinding(progressBar, RangeBase.ValueProperty);
            Assert(progressBinding?.Mode == BindingMode.OneWay,
                "read-only download progress is bound one-way");

            var acceptedDirectory = Path.Combine(testRoot, "accepted-downloads");
            string? persistedDirectory = null;
            var acceptedSettings = new SettingsWindow(
                acceptedDirectory,
                directory =>
                {
                    persistedDirectory = directory;
                    return Task.CompletedTask;
                });
            Assert(acceptedSettings.TrySaveAsync().GetAwaiter().GetResult(),
                "the settings window accepts a valid directory after persistence succeeds");
            Assert(persistedDirectory == Path.GetFullPath(acceptedDirectory),
                "the settings window persists the normalized directory before closing");
            acceptedSettings.Close();

            var rejectedSettings = new SettingsWindow(
                Path.Combine(testRoot, "rejected-downloads"),
                _ => Task.FromException(new IOException("simulated settings write failure")))
            {
                Width = 400,
                Height = 260
            };
            Assert(!rejectedSettings.TrySaveAsync().GetAwaiter().GetResult(),
                "the settings window stays open when persistence fails");
            Assert(rejectedSettings.FindName("ErrorText") is System.Windows.Controls.TextBlock
            {
                Visibility: Visibility.Visible
            },
                "a settings persistence failure is shown inline");

            RenderWindow(
                new SettingsWindow(Path.Combine(
                    KnownFolders.DownloadsDirectory,
                    "long-folder-name-for-layout-review",
                    "downloads")),
                Path.Combine(outputDirectory, "settings.png"));
            RenderWindow(rejectedSettings, Path.Combine(outputDirectory, "settings-error-narrow.png"));
            var aboutWindow = new AboutWindow();
            RenderWindow(aboutWindow, Path.Combine(outputDirectory, "about.png"));
            RenderWindow(
                new AboutWindow
                {
                    Width = 480,
                    Height = 300
                },
                Path.Combine(outputDirectory, "about-narrow.png"));
            application.Shutdown();
        }
        catch (Exception exception)
        {
            failure = exception;
        }
    });
    thread.SetApartmentState(ApartmentState.STA);
    thread.Start();
    thread.Join();

    if (failure is not null)
    {
        throw new InvalidOperationException("FAIL: settings/about windows could not be rendered", failure);
    }

    Console.WriteLine($"  ok: application windows render with safe one-way bindings to {outputDirectory}");
}

static T? FindVisualDescendant<T>(DependencyObject root)
    where T : DependencyObject
{
    for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
    {
        var child = VisualTreeHelper.GetChild(root, index);
        if (child is T match)
        {
            return match;
        }

        if (FindVisualDescendant<T>(child) is { } descendant)
        {
            return descendant;
        }
    }

    return null;
}

static void RenderWindow(Window window, string outputPath)
{
    var content = window.Content as FrameworkElement
        ?? throw new InvalidOperationException($"FAIL: {window.Title} has no renderable content");
    var logicalWidth = window.Width;
    var logicalHeight = window.Height;
    content.Measure(new Size(logicalWidth, logicalHeight));
    content.Arrange(new Rect(0, 0, logicalWidth, logicalHeight));
    content.UpdateLayout();

    var dpi = VisualTreeHelper.GetDpi(content);
    var width = Math.Max(1, (int)Math.Ceiling(logicalWidth * dpi.DpiScaleX));
    var height = Math.Max(1, (int)Math.Ceiling(logicalHeight * dpi.DpiScaleY));
    var contentBitmap = new RenderTargetBitmap(
        width,
        height,
        dpi.PixelsPerInchX,
        dpi.PixelsPerInchY,
        PixelFormats.Pbgra32);
    contentBitmap.Render(content);

    var surface = new DrawingVisual();
    using (var drawingContext = surface.RenderOpen())
    {
        drawingContext.DrawRectangle(
            window.Background ?? Brushes.White,
            null,
            new Rect(0, 0, logicalWidth, logicalHeight));
        drawingContext.DrawImage(contentBitmap, new Rect(0, 0, logicalWidth, logicalHeight));
    }

    var bitmap = new RenderTargetBitmap(width, height, dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
    bitmap.Render(surface);
    var encoder = new PngBitmapEncoder();
    encoder.Frames.Add(BitmapFrame.Create(bitmap));
    using (var stream = File.Create(outputPath))
    {
        encoder.Save(stream);
    }

    Assert(new FileInfo(outputPath).Length > 1000, $"{window.Title} produces a non-empty UI snapshot");
}

static DownloadJob NewPartialJob(string targetPath, long totalBytes) => new()
{
    Url = "https://mirror.test/file.bin",
    FileName = Path.GetFileName(targetPath),
    DestinationFolder = Path.GetDirectoryName(targetPath)!,
    ResolvedTargetPath = targetPath,
    TotalBytes = totalBytes,
    SegmentCount = 2,
    SupportsRanges = true,
    State = DownloadState.Paused
};

static void Assert(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException("FAIL: " + message);
    }
}

static async Task AssertThrowsAsync<TException>(Func<Task> action, string message)
    where TException : Exception
{
    try
    {
        await action();
    }
    catch (TException)
    {
        return;
    }

    throw new InvalidOperationException("FAIL: " + message);
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
