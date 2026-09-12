using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using BigFileDownloader.Models;
using BigFileDownloader.Services;
using BigFileDownloader.Views;

var projectRoot = FindProjectRoot();
var root = Path.Combine(projectRoot, "artifacts", "selftest-temp", $"downloader-selftest-{Guid.NewGuid():N}");
Directory.CreateDirectory(root);

try
{
    await TestExpiringRedirectAsync(root);
    TestRemoteIdentityComparison();
    await TestPartialMergeAsync(root);
    TestArtifactDeletion(root);
    TestArtifactPathAliases(root);
    await TestInterruptedDeletionRecoveryAsync(root);
    TestPortableApplicationData(root);
    await TestSettingsStoreAsync(root);
    TestAppInfo();
    TestUiRendering(root, Path.Combine(projectRoot, "artifacts", "ui-review"));
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
    Assert(job.TargetFileOwnership == TargetFileOwnership.CreatedByDownloader,
        "a successfully moved download is marked as downloader-owned");
    Assert(job.TargetFileFingerprint is not null,
        "a successfully moved download records the target file identity");
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

static void TestArtifactDeletion(string root)
{
    var directory = Path.Combine(root, "artifact-deletion");
    Directory.CreateDirectory(directory);
    var targetPath = Path.Combine(directory, "download.bin");
    var siblingPath = Path.Combine(directory, "keep-me.txt");
    var job = NewPartialJob(targetPath, 1000);
    string? recycledTarget = null;
    using var engine = new DownloadEngine(recycleTargetFile: path =>
    {
        recycledTarget = path;
        File.Delete(path);
    });

    File.WriteAllText(targetPath, "completed data");
    job.TargetFileFingerprint = PhysicalPathResolver.GetFileFingerprint(targetPath);
    job.TargetFileOwnership = TargetFileOwnership.CreatedByDownloader;
    File.WriteAllText(siblingPath, "unrelated data");
    CreateTemporaryArtifacts(targetPath);
    engine.DeletePartialData(job);
    Assert(File.Exists(targetPath), "partial cleanup keeps the completed target file");
    Assert(File.Exists(siblingPath), "partial cleanup keeps unrelated sibling files");
    Assert(!Directory.Exists(targetPath + ".bfdl.parts"), "partial cleanup removes the segment directory");
    Assert(!File.Exists(targetPath + ".bfdl.tmp"), "partial cleanup removes the single-stream temporary file");
    Assert(!File.Exists(targetPath + ".assembling"), "partial cleanup removes the assembly file");

    CreateTemporaryArtifacts(targetPath);
    engine.DeleteDownloadData(job);
    Assert(recycledTarget == targetPath, "full cleanup uses the production recycle-file path");
    Assert(!File.Exists(targetPath), "full cleanup removes the exact target file");
    Assert(File.Exists(siblingPath), "full cleanup never removes another file in the destination");
    Assert(Directory.Exists(directory), "full cleanup never removes the destination folder");

    var externalTarget = Path.Combine(directory, "external.bin");
    var externalJob = NewPartialJob(externalTarget, 1000);
    externalJob.TargetFileOwnership = TargetFileOwnership.ExistingFile;
    File.WriteAllText(externalTarget, "external data");
    File.WriteAllText(externalTarget + ".bfdl.tmp", "temporary data");
    recycledTarget = null;
    var externalResult = engine.DeleteDownloadData(externalJob);
    Assert(File.Exists(externalTarget), "full cleanup preserves a target known to be an external file");
    Assert(!File.Exists(externalTarget + ".bfdl.tmp"),
        "full cleanup still removes task-owned temporary data beside an external target");
    Assert(externalResult.PreservedTargetPath == externalTarget && recycledTarget is null,
        "external target preservation is reported without invoking the recycle operation");

    var replacedTarget = Path.Combine(directory, "replaced.bin");
    var replacedJob = NewPartialJob(replacedTarget, 1000);
    File.WriteAllText(replacedTarget, "original downloader data");
    replacedJob.TargetFileFingerprint = PhysicalPathResolver.GetFileFingerprint(replacedTarget);
    replacedJob.TargetFileOwnership = TargetFileOwnership.CreatedByDownloader;
    File.Delete(replacedTarget);
    File.WriteAllText(replacedTarget, "private replacement data with a different length");
    File.WriteAllText(replacedTarget + ".bfdl.tmp", "temporary data");
    recycledTarget = null;
    var replacedResult = engine.DeleteDownloadData(replacedJob);
    Assert(File.Exists(replacedTarget), "full cleanup preserves a file that replaced the downloader-owned target");
    Assert(!File.Exists(replacedTarget + ".bfdl.tmp"),
        "full cleanup removes task temporary data after preserving a replaced target");
    Assert(replacedResult.PreservedTargetPath == replacedTarget && recycledTarget is null,
        "a replaced target never reaches the recycle operation");

    var legacyTarget = Path.Combine(directory, "legacy.bin");
    var legacyJob = NewPartialJob(legacyTarget, 1000);
    File.WriteAllText(legacyTarget, "legacy task target with unknown ownership");
    File.WriteAllText(legacyTarget + ".bfdl.tmp", "temporary data");
    recycledTarget = null;
    var legacyResult = engine.DeleteDownloadData(legacyJob);
    Assert(File.Exists(legacyTarget), "full cleanup preserves an old task target with unknown ownership");
    Assert(!File.Exists(legacyTarget + ".bfdl.tmp"),
        "full cleanup still removes known temporary data for an old task");
    Assert(legacyResult.PreservedTargetPath == legacyTarget && recycledTarget is null,
        "unknown target ownership never reaches the recycle operation");

    var guardedTarget = Path.Combine(directory, "guarded.bin");
    var guardedJob = NewPartialJob(guardedTarget, 1000);
    File.WriteAllText(guardedTarget, "completed data");
    var guardedParts = guardedTarget + ".bfdl.parts";
    Directory.CreateDirectory(guardedParts);
    File.WriteAllText(Path.Combine(guardedParts, "part-000.tmp"), "partial data");
    var unknownPath = Path.Combine(guardedParts, "personal-note.txt");
    File.WriteAllText(unknownPath, "do not delete");
    AssertThrows<IOException>(
        () => engine.DeleteDownloadData(guardedJob),
        "unknown content prevents recursive partial-data deletion");
    Assert(File.Exists(guardedTarget), "a guarded cleanup failure keeps the completed target");
    Assert(File.Exists(unknownPath), "a guarded cleanup failure keeps unknown files");

    var outOfRangeTarget = Path.Combine(directory, "out-of-range-part.bin");
    var outOfRangeJob = NewPartialJob(outOfRangeTarget, 1000);
    var outOfRangeParts = outOfRangeTarget + ".bfdl.parts";
    Directory.CreateDirectory(outOfRangeParts);
    var outOfRangePart = Path.Combine(outOfRangeParts, "part-999.tmp");
    File.WriteAllText(outOfRangePart, "another task's possible data");
    AssertThrows<IOException>(
        () => engine.DeleteDownloadData(outOfRangeJob),
        "a numeric part outside the task's segment range blocks deletion");
    Assert(File.Exists(outOfRangePart), "an out-of-range numeric part is never deleted");

    var directoryTarget = Path.Combine(directory, "unexpected-directory.bin");
    var directoryTargetJob = NewPartialJob(directoryTarget, 1000);
    Directory.CreateDirectory(directoryTarget);
    var directoryTargetTemporary = directoryTarget + ".bfdl.tmp";
    File.WriteAllText(directoryTargetTemporary, "resumable data");
    AssertThrows<IOException>(
        () => engine.DeleteDownloadData(directoryTargetJob),
        "an unexpected target directory blocks deletion before temporary data is touched");
    Assert(File.Exists(directoryTargetTemporary),
        "target preflight failure preserves resumable temporary data");

    recycledTarget = null;
    var unavailableDirectory = Path.Combine(directory, "unavailable-destination");
    var unavailableJob = NewPartialJob(Path.Combine(unavailableDirectory, "offline.bin"), 1000);
    AssertThrows<IOException>(
        () => engine.DeleteDownloadData(unavailableJob),
        "an unavailable destination is not mistaken for an already-deleted file");
    Assert(recycledTarget is null,
        "an unavailable destination never reaches the recycle-file operation");

    var relativeJob = NewPartialJob(Path.Combine("relative", "file.bin"), 1000);
    AssertThrows<InvalidOperationException>(
        () => DownloadArtifactPlan.CreateFor(relativeJob),
        "relative task paths cannot be used for deletion");
    var outsideJob = NewPartialJob(Path.Combine(directory, "outside.bin"), 1000);
    outsideJob.DestinationFolder = Path.Combine(directory, "different-folder");
    AssertThrows<InvalidOperationException>(
        () => DownloadArtifactPlan.CreateFor(outsideJob),
        "a target outside its recorded destination cannot be used for deletion");

    var overlapBase = DownloadArtifactPlan.CreateForTargetPath(Path.Combine(directory, "overlap.bin"));
    Directory.CreateDirectory(overlapBase.PartsDirectory);
    var nestedTarget = DownloadArtifactPlan.CreateForTargetPath(
        Path.Combine(overlapBase.PartsDirectory, "part-000.tmp"));
    var unrelatedTarget = DownloadArtifactPlan.CreateForTargetPath(Path.Combine(directory, "unrelated.bin"));
    Assert(overlapBase.Overlaps(nestedTarget),
        "a task target inside another task's part directory is treated as shared data");
    Assert(!overlapBase.Overlaps(unrelatedTarget),
        "independent target paths do not trigger the shared-data guard");
    Assert(overlapBase.ConflictsWithPendingDestination(directory),
        "an unresolved active task in the target directory blocks artifact deletion");
    Assert(overlapBase.ConflictsWithPendingDestination(overlapBase.ComparisonPartsDirectory),
        "an unresolved active task inside the segment directory blocks artifact deletion");
    Assert(!overlapBase.ConflictsWithPendingDestination(Path.Combine(directory, "independent-downloads")),
        "an unresolved active task in an independent directory does not conflict");

    var upperCasePlan = DownloadArtifactPlan.CreateForTargetPath(Path.Combine(directory, "CaseOnly.bin"));
    var lowerCasePlan = DownloadArtifactPlan.CreateForTargetPath(Path.Combine(directory, "caseonly.bin"));
    Assert(!upperCasePlan.HasSameArtifacts(lowerCasePlan),
        "case-only target names remain separate artifact identities");
    Assert(upperCasePlan.Overlaps(lowerCasePlan),
        "case-only target names are conservatively treated as a possible filesystem conflict");

    var reservedAssembly = Path.Combine(directory, "reserved.bin.assembling");
    File.WriteAllText(reservedAssembly, "unfinished assembly");
    Assert(Path.GetFileName(FileNameHelper.EnsureUniquePath(directory, "reserved.bin")) == "reserved (1).bin",
        "a leftover assembly file reserves its task target name");

    Console.WriteLine("  ok: task cleanup is exact, guarded, and preserves unrelated files");
}

static void CreateTemporaryArtifacts(string targetPath)
{
    var partsDirectory = targetPath + ".bfdl.parts";
    Directory.CreateDirectory(partsDirectory);
    File.WriteAllText(Path.Combine(partsDirectory, "part-000.tmp"), "partial data");
    File.WriteAllText(targetPath + ".bfdl.tmp", "single-stream data");
    File.WriteAllText(targetPath + ".assembling", "assembly data");
}

static void TestArtifactPathAliases(string root)
{
    var testDirectory = Path.Combine(root, "artifact-path-aliases");
    var realDirectory = Path.Combine(testDirectory, "real");
    var switchedDirectory = Path.Combine(testDirectory, "switched");
    var aliasDirectory = Path.Combine(testDirectory, "alias");
    Directory.CreateDirectory(realDirectory);
    Directory.CreateDirectory(switchedDirectory);

    try
    {
        Directory.CreateSymbolicLink(aliasDirectory, realDirectory);
    }
    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
    {
        Console.WriteLine("  skip: directory-link identity test is unavailable on this Windows configuration");
        return;
    }

    try
    {
        var realPlan = DownloadArtifactPlan.CreateForTargetPath(Path.Combine(realDirectory, "shared.bin"));
        var aliasPlan = DownloadArtifactPlan.CreateForTargetPath(Path.Combine(aliasDirectory, "shared.bin"));
        Assert(realPlan.HasSameTarget(aliasPlan),
            "directory aliases resolve to the same physical task target");
        Assert(realPlan.Overlaps(aliasPlan),
            "directory aliases cannot bypass the shared-data deletion guard");

        var linkedFileSource = Path.Combine(realDirectory, "linked-source.bin");
        var linkedFileTarget = Path.Combine(realDirectory, "linked-target.bin");
        File.WriteAllText(linkedFileSource, "external linked data");
        File.CreateSymbolicLink(linkedFileTarget, linkedFileSource);
        var linkedFileJob = NewPartialJob(linkedFileTarget, 1000);
        using (var linkedFileEngine = new DownloadEngine(recycleTargetFile: File.Delete))
        {
            AssertThrows<IOException>(
                () => linkedFileEngine.DeleteDownloadData(linkedFileJob),
                "a symbolic-link target is rejected before deletion");
        }

        Assert(File.Exists(linkedFileSource) && File.Exists(linkedFileTarget),
            "rejecting a symbolic-link target preserves both the link and its destination");

        var linkedSourceJob = NewPartialJob(linkedFileSource, 1000);
        var linkedSourcePlan = DownloadArtifactPlan.CreateFor(linkedSourceJob)
            ?? throw new InvalidOperationException("FAIL: linked source task plan was not created");
        File.WriteAllText(linkedFileSource + ".bfdl.tmp", "source-side temporary data");
        File.WriteAllText(linkedFileTarget + ".bfdl.tmp", "link-side temporary data");
        using (var groupedEngine = new DownloadEngine(recycleTargetFile: File.Delete))
        {
            AssertThrows<IOException>(
                () => groupedEngine.DeleteDownloadData([linkedSourceJob, linkedFileJob], linkedSourcePlan),
                "a grouped real target and symbolic-link target are rejected together");
        }

        Assert(File.Exists(linkedFileSource + ".bfdl.tmp")
            && File.Exists(linkedFileTarget + ".bfdl.tmp"),
            "a grouped symbolic-link rejection preserves both tasks' temporary data");
        File.Delete(linkedFileTarget);

        var sidecarTaskTarget = Path.Combine(realDirectory, "sidecar-link-task.bin");
        var privateSidecarTarget = Path.Combine(realDirectory, "private-sidecar-data.txt");
        var sidecarLink = sidecarTaskTarget + ".bfdl.tmp";
        File.WriteAllText(privateSidecarTarget, "private sidecar destination");
        File.CreateSymbolicLink(sidecarLink, privateSidecarTarget);
        var sidecarLinkJob = NewPartialJob(sidecarTaskTarget, 1000);
        using (var sidecarEngine = new DownloadEngine(recycleTargetFile: File.Delete))
        {
            AssertThrows<IOException>(
                () => sidecarEngine.DeleteDownloadData(sidecarLinkJob),
                "a symbolic-link temporary artifact blocks deletion");
        }

        Assert(File.Exists(sidecarLink) && File.Exists(privateSidecarTarget),
            "rejecting a symbolic-link temporary artifact preserves the link destination");
        File.Delete(sidecarLink);

        var longTarget = Path.Combine(realDirectory, "long-file-name-for-short-path-alias.bin");
        File.WriteAllText(longTarget, "short-path identity test");
        var shortTarget = WindowsPathNames.TryGetShortPath(longTarget);
        if (shortTarget is not null
            && !string.Equals(shortTarget, longTarget, StringComparison.OrdinalIgnoreCase))
        {
            var longJob = NewPartialJob(longTarget, 1000);
            var shortJob = NewPartialJob(shortTarget, 1000);
            var longPlan = DownloadArtifactPlan.CreateFor(longJob)
                ?? throw new InvalidOperationException("FAIL: long-name task plan was not created");
            var shortPlan = DownloadArtifactPlan.CreateForTargetPath(shortTarget);
            Assert(longPlan.HasSameTarget(shortPlan),
                "short and long target aliases resolve to the same target identity");
            Assert(!longPlan.HasSameArtifacts(shortPlan),
                "short and long target aliases retain distinct temporary artifact identities");
            File.WriteAllText(longTarget + ".bfdl.tmp", "long-name temporary data");
            File.WriteAllText(shortTarget + ".bfdl.tmp", "short-name temporary data");
            using var shortNameEngine = new DownloadEngine(recycleTargetFile: File.Delete);
            AssertThrows<IOException>(
                () => shortNameEngine.DeleteDownloadData([longJob, shortJob], longPlan),
                "short and long target aliases cannot be deleted as one artifact group");
            Assert(File.Exists(longTarget + ".bfdl.tmp") && File.Exists(shortTarget + ".bfdl.tmp"),
                "rejecting short-name aliases preserves both temporary artifact sets");
        }
        else
        {
            Console.WriteLine("  skip: NTFS short-name identity test is unavailable on this volume");
        }

        var identityDirectory = Path.Combine(testDirectory, "directory-identity");
        var movedIdentityDirectory = Path.Combine(testDirectory, "directory-identity-original");
        Directory.CreateDirectory(identityDirectory);
        var originalDirectoryPlan = DownloadArtifactPlan.CreateForTargetPath(
            Path.Combine(identityDirectory, "identity.bin"));
        Directory.Move(identityDirectory, movedIdentityDirectory);
        Directory.CreateDirectory(identityDirectory);
        var replacementDirectoryPlan = DownloadArtifactPlan.CreateForTargetPath(
            Path.Combine(identityDirectory, "identity.bin"));
        Assert(!originalDirectoryPlan.HasSameTarget(replacementDirectoryPlan),
            "a replacement directory at the same path has a different artifact identity");

        var originalTarget = Path.Combine(realDirectory, "switch.bin");
        var switchedTarget = Path.Combine(switchedDirectory, "switch.bin");
        File.WriteAllText(originalTarget, "original target");
        File.WriteAllText(switchedTarget, "retained target");
        var switchingJob = NewPartialJob(Path.Combine(aliasDirectory, "switch.bin"), 1000);
        var frozenPlan = DownloadArtifactPlan.CreateFor(switchingJob)
            ?? throw new InvalidOperationException("FAIL: alias task plan was not created");
        switchingJob.TargetFileFingerprint = PhysicalPathResolver.GetFileFingerprint(originalTarget);
        switchingJob.TargetFileOwnership = TargetFileOwnership.CreatedByDownloader;

        Directory.Delete(aliasDirectory);
        Directory.CreateSymbolicLink(aliasDirectory, switchedDirectory);
        using var engine = new DownloadEngine(recycleTargetFile: File.Delete);
        AssertThrows<IOException>(
            () => engine.DeleteDownloadData([switchingJob], frozenPlan),
            "a directory alias switched after confirmation blocks deletion");
        Assert(File.Exists(originalTarget) && File.Exists(switchedTarget),
            "a switched directory alias leaves both possible targets untouched");
    }
    finally
    {
        Directory.Delete(aliasDirectory);
    }

    Console.WriteLine("  ok: directory aliases are resolved before task artifacts are compared");
}

static async Task TestInterruptedDeletionRecoveryAsync(string root)
{
    var queuePath = Path.Combine(root, "deletion-recovery", "queue.json");
    var store = new DownloadQueueStore(queuePath);
    var job = NewPartialJob(Path.Combine(root, "deletion-recovery", "file.bin"), 1000);
    job.TargetFileOwnership = TargetFileOwnership.CreatedByDownloader;
    job.TargetFileFingerprint = new TargetFileFingerprint("00000001:0000000000000002", 1000, 3);
    job.State = DownloadState.Deleting;
    job.Message = "正在准备删除任务文件";
    await store.SaveAsync([job]);

    var restored = await store.LoadAsync();
    Assert(restored.Count == 1 && restored[0].State == DownloadState.DeletionFailed,
        "an interrupted deletion is restored as a recoverable failure");
    Assert(restored[0].Message.Contains("删除未完成", StringComparison.Ordinal),
        "an interrupted deletion explains that the file location must be checked");
    Assert(restored[0].TargetFileOwnership == TargetFileOwnership.CreatedByDownloader,
        "target ownership survives an interrupted-deletion queue round trip");
    Assert(restored[0].TargetFileFingerprint == job.TargetFileFingerprint,
        "the target file identity survives an interrupted-deletion queue round trip");

    Console.WriteLine("  ok: interrupted deletion checkpoints recover without a false completed state");
}

static void TestPortableApplicationData(string root)
{
    var applicationBase = Path.GetFullPath(AppContext.BaseDirectory)
        .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
        + Path.DirectorySeparatorChar;
    Assert(ApplicationDataPaths.RootDirectory.StartsWith(applicationBase, StringComparison.OrdinalIgnoreCase),
        "default application data stays beside the executable");
    Assert(ApplicationDataPaths.QueueFilePath.StartsWith(ApplicationDataPaths.RootDirectory, StringComparison.OrdinalIgnoreCase)
        && ApplicationDataPaths.SettingsFilePath.StartsWith(ApplicationDataPaths.RootDirectory, StringComparison.OrdinalIgnoreCase)
        && ApplicationDataPaths.LogsDirectory.StartsWith(ApplicationDataPaths.RootDirectory, StringComparison.OrdinalIgnoreCase),
        "queue, settings, and logs share the portable data directory");
    Assert(ApplicationDataPaths.DefaultDownloadDirectory.StartsWith(applicationBase, StringComparison.OrdinalIgnoreCase),
        "a clean installation defaults downloads beside the executable");

    var legacyRoot = Path.Combine(root, "legacy-local-app-data");
    var legacyApplicationDirectory = Path.Combine(legacyRoot, "BigFileDownloader");
    var legacyLogsDirectory = Path.Combine(legacyRoot, "downloader", "logs");
    var destinationRoot = Path.Combine(root, "portable-application-data");
    Directory.CreateDirectory(legacyApplicationDirectory);
    Directory.CreateDirectory(legacyLogsDirectory);
    Directory.CreateDirectory(destinationRoot);

    File.WriteAllText(Path.Combine(legacyApplicationDirectory, "queue.json"), "legacy queue");
    File.WriteAllText(Path.Combine(legacyApplicationDirectory, "settings.json"), "portable settings");
    File.WriteAllText(Path.Combine(legacyLogsDirectory, "downloader-20260912.log"), "legacy log");
    File.WriteAllText(Path.Combine(destinationRoot, "queue.json"), "current queue");

    var migration = ApplicationDataPaths.MigrateLegacyData(legacyRoot, destinationRoot);
    Assert(migration.MigratedFileCount == 3 && migration.Warnings.Count == 0 && !migration.HasBlockingFailure,
        "legacy application files migrate without data loss warnings");
    Assert(File.ReadAllText(Path.Combine(destinationRoot, "queue.json")) == "current queue",
        "an existing portable queue remains authoritative during migration");
    var legacyQueueBackups = Directory.GetFiles(destinationRoot, "queue.legacy-*.json");
    Assert(legacyQueueBackups.Length == 1 && File.ReadAllText(legacyQueueBackups[0]) == "legacy queue",
        "a conflicting legacy queue is retained as a portable backup");
    Assert(File.ReadAllText(Path.Combine(destinationRoot, "settings.json")) == "portable settings"
        && File.ReadAllText(Path.Combine(destinationRoot, "logs", "downloader-20260912.log")) == "legacy log",
        "settings and logs move into the portable data directory");
    Assert(!Directory.Exists(legacyApplicationDirectory)
        && !Directory.Exists(legacyLogsDirectory)
        && !Directory.Exists(Path.Combine(legacyRoot, "downloader")),
        "empty legacy application directories are removed after migration");

    var recoveryLegacyRoot = Path.Combine(root, "recovery-local-app-data");
    var recoveryLegacyApplicationDirectory = Path.Combine(recoveryLegacyRoot, "BigFileDownloader");
    var recoveryDestinationRoot = Path.Combine(root, "recovery-portable-application-data");
    Directory.CreateDirectory(recoveryLegacyApplicationDirectory);
    Directory.CreateDirectory(recoveryDestinationRoot);
    File.WriteAllText(Path.Combine(recoveryLegacyApplicationDirectory, "queue.json"), "[{\"id\":\"legacy-task\"}]");
    File.WriteAllText(Path.Combine(recoveryDestinationRoot, "queue.json"), "[]");

    var recoveryMigration = ApplicationDataPaths.MigrateLegacyData(recoveryLegacyRoot, recoveryDestinationRoot);
    Assert(!recoveryMigration.HasBlockingFailure
        && File.ReadAllText(Path.Combine(recoveryDestinationRoot, "queue.json")).Contains("legacy-task"),
        "a non-empty legacy queue replaces an empty portable queue after an interrupted migration");
    var emptyQueueBackups = Directory.GetFiles(recoveryDestinationRoot, "queue.legacy-*.json");
    Assert(emptyQueueBackups.Length == 1 && File.ReadAllText(emptyQueueBackups[0]) == "[]",
        "the replaced empty portable queue remains available as a migration backup");

    var lockedLegacyRoot = Path.Combine(root, "locked-local-app-data");
    var lockedLegacyApplicationDirectory = Path.Combine(lockedLegacyRoot, "BigFileDownloader");
    var lockedDestinationRoot = Path.Combine(root, "locked-portable-application-data");
    Directory.CreateDirectory(lockedLegacyApplicationDirectory);
    Directory.CreateDirectory(lockedDestinationRoot);
    var lockedQueuePath = Path.Combine(lockedLegacyApplicationDirectory, "queue.json");
    File.WriteAllText(lockedQueuePath, "[{\"id\":\"locked-legacy-task\"}]");
    File.WriteAllText(Path.Combine(lockedDestinationRoot, "queue.json"), "[]");
    using (File.Open(lockedQueuePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
    {
        var blockedMigration = ApplicationDataPaths.MigrateLegacyData(lockedLegacyRoot, lockedDestinationRoot);
        Assert(blockedMigration.HasBlockingFailure && blockedMigration.Warnings.Count == 1,
            "a locked legacy queue blocks startup even when a portable queue already exists");
    }

    var retriedMigration = ApplicationDataPaths.MigrateLegacyData(lockedLegacyRoot, lockedDestinationRoot);
    Assert(!retriedMigration.HasBlockingFailure
        && File.ReadAllText(Path.Combine(lockedDestinationRoot, "queue.json")).Contains("locked-legacy-task"),
        "a blocked migration succeeds without data loss after the legacy queue is released");

    var repeatedMigration = ApplicationDataPaths.MigrateLegacyData(legacyRoot, destinationRoot);
    Assert(repeatedMigration.MigratedFileCount == 0 && repeatedMigration.Warnings.Count == 0,
        "portable data migration is idempotent");

    Console.WriteLine("  ok: application data is portable and legacy files migrate safely");
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
    Assert(futureFallback.DefaultDownloadDirectory == ApplicationDataPaths.DefaultDownloadDirectory,
        "an unknown settings schema falls back to Downloads");
    Assert(await File.ReadAllTextAsync(settingsPath) == futureSettings,
        "an unknown settings schema is not overwritten");

    const string invalidSettings = "{not-valid-json";
    await File.WriteAllTextAsync(settingsPath, invalidSettings);
    var invalidFallback = await store.LoadAsync();
    Assert(invalidFallback.DefaultDownloadDirectory == ApplicationDataPaths.DefaultDownloadDirectory,
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
            var firstJob = new DownloadJob
            {
                FileName = "existing-download.bin",
                DestinationFolder = ApplicationDataPaths.DefaultDownloadDirectory,
                TotalBytes = 1_000,
                DownloadedBytes = 500,
                State = DownloadState.Paused,
                Message = "已保留分块，可继续下载"
            };
            var secondJob = new DownloadJob
            {
                FileName = "second-download.bin",
                DestinationFolder = ApplicationDataPaths.DefaultDownloadDirectory,
                TotalBytes = 2_000,
                DownloadedBytes = 250,
                State = DownloadState.Failed,
                Message = "等待重试"
            };
            mainWindow.Jobs.Add(firstJob);
            mainWindow.Jobs.Add(secondJob);
            RenderWindow(mainWindow, Path.Combine(outputDirectory, "main-with-existing-task.png"));
            var progressBar = FindVisualDescendant<ProgressBar>(mainWindow.QueueGrid)
                ?? throw new InvalidOperationException("FAIL: the download progress bar was not rendered");
            var progressBinding = BindingOperations.GetBinding(progressBar, RangeBase.ValueProperty);
            Assert(progressBinding?.Mode == BindingMode.OneWay,
                "read-only download progress is bound one-way");
            var taskRow = mainWindow.QueueGrid.ItemContainerGenerator.ContainerFromIndex(0) as DataGridRow;
            Assert(taskRow?.ContextMenu is not null, "each download row exposes a context menu");
            var taskMenuItems = taskRow!.ContextMenu!.Items.OfType<MenuItem>().ToArray();
            Assert(taskMenuItems.Length == 2 && Equals(taskMenuItems[0].Header, "打开文件位置"),
                "the task context menu exposes open-location first");
            Assert(Equals(taskMenuItems[1].Header, "删除")
                && taskMenuItems[1].Items.OfType<MenuItem>().Select(item => item.Header).SequenceEqual(
                    new object[] { "仅删除任务记录...", "删除任务记录及文件..." }),
                "the task context menu separates record-only and file deletion");
            var secondRow = mainWindow.QueueGrid.ItemContainerGenerator.ContainerFromIndex(1) as DataGridRow
                ?? throw new InvalidOperationException("FAIL: the second task row was not rendered");
            mainWindow.QueueGrid.SelectedItems.Clear();
            mainWindow.QueueGrid.SelectedItems.Add(firstJob);
            BigFileDownloader.MainWindow.SelectContextRow(mainWindow.QueueGrid, secondRow);
            Assert(mainWindow.QueueGrid.SelectedItems.Count == 1
                && mainWindow.QueueGrid.SelectedItems.Contains(secondJob),
                "right-clicking an unselected row replaces the previous selection");
            mainWindow.QueueGrid.SelectedItems.Clear();
            mainWindow.QueueGrid.SelectedItems.Add(firstJob);
            mainWindow.QueueGrid.SelectedItems.Add(secondJob);
            BigFileDownloader.MainWindow.SelectContextRow(mainWindow.QueueGrid, secondRow);
            Assert(mainWindow.QueueGrid.SelectedItems.Count == 2,
                "right-clicking within an existing multi-selection preserves the selection");

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
                    ApplicationDataPaths.DefaultDownloadDirectory,
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

static string FindProjectRoot()
{
    for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
    {
        if (File.Exists(Path.Combine(directory.FullName, "BigFileDownloader.csproj")))
        {
            return directory.FullName;
        }
    }

    throw new DirectoryNotFoundException("无法定位 BigFileDownloader 项目目录。");
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

static void AssertThrows<TException>(Action action, string message)
    where TException : Exception
{
    try
    {
        action();
    }
    catch (TException)
    {
        return;
    }

    throw new InvalidOperationException("FAIL: " + message);
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

internal static class WindowsPathNames
{
    public static string? TryGetShortPath(string path)
    {
        var requiredLength = GetShortPathName(path, null, 0);
        if (requiredLength == 0)
        {
            return null;
        }

        var buffer = new StringBuilder(checked((int)requiredLength));
        return GetShortPathName(path, buffer, (uint)buffer.Capacity) == 0
            ? null
            : buffer.ToString();
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetShortPathName(
        string longPath,
        StringBuilder? shortPath,
        uint bufferLength);
}
