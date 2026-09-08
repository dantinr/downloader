using BigFileDownloader.Models;

namespace BigFileDownloader.Services;

internal sealed record PartialMergeResult(long RecoveredBytes, long ComparedBytes);

internal sealed class PartialDownloadMergeService
{
    private const int BufferSize = 1024 * 1024;

    public long GetRecoverableBytes(DownloadJob job)
    {
        var ranges = GetRanges(job);
        var partsDirectory = GetPartsDirectory(job);
        if (!Directory.Exists(partsDirectory))
        {
            return 0;
        }

        return ranges.Sum(range => GetValidatedPartLength(partsDirectory, range));
    }

    public async Task<PartialMergeResult> ValidateRedundantTaskAsync(
        DownloadJob target,
        DownloadJob redundant,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(redundant);

        ValidateTasks(target, redundant);

        var ranges = GetRanges(target);
        var targetPartsDirectory = GetPartsDirectory(target);
        var redundantPartsDirectory = GetPartsDirectory(redundant);
        var samePartsDirectory = PathsEqual(targetPartsDirectory, redundantPartsDirectory);
        long recoveredBytes = 0;
        long comparedBytes = 0;

        foreach (var range in ranges)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var targetLength = GetValidatedPartLength(targetPartsDirectory, range);
            var redundantLength = samePartsDirectory
                ? targetLength
                : GetValidatedPartLength(redundantPartsDirectory, range);

            if (redundantLength > targetLength)
            {
                throw new InvalidOperationException(
                    $"另一任务的分块 {range.Index + 1} 含有当前保留任务没有的进度。" +
                    "在缺少整文件 Hash 时不能安全拼接，请先继续下载进度更多的任务。");
            }

            if (!samePartsDirectory && redundantLength > 0)
            {
                await EnsureContentsMatchAsync(
                    PartPath(targetPartsDirectory, range.Index),
                    PartPath(redundantPartsDirectory, range.Index),
                    redundantLength,
                    cancellationToken);
                comparedBytes += redundantLength;
            }

            recoveredBytes += targetLength;
        }

        if (!samePartsDirectory && comparedBytes == 0)
        {
            throw new InvalidOperationException("被移除任务没有可校验的已下载内容，无法确认两个任务属于同一文件。");
        }

        DiagnosticLog.Info(
            "Merge",
            $"Redundant task validated; target={target.Id:N}; redundant={redundant.Id:N}; " +
            $"recoveredBytes={recoveredBytes}; comparedBytes={comparedBytes}; samePartsDirectory={samePartsDirectory}");
        return new PartialMergeResult(recoveredBytes, comparedBytes);
    }

    private static void ValidateTasks(DownloadJob target, DownloadJob redundant)
    {
        if (ReferenceEquals(target, redundant) || target.Id == redundant.Id)
        {
            throw new InvalidOperationException("不能将任务与自身整合。");
        }

        if (target.ActiveTask is { IsCompleted: false } || redundant.ActiveTask is { IsCompleted: false })
        {
            throw new InvalidOperationException("任务仍在下载，请先等待任务完全暂停。");
        }

        if (target.TotalBytes is not > 0 || redundant.TotalBytes is not > 0
            || target.TotalBytes != redundant.TotalBytes)
        {
            throw new InvalidOperationException("两个任务报告的文件大小不同或未知，不能整合。");
        }

        if (!target.SupportsRanges || !redundant.SupportsRanges)
        {
            throw new InvalidOperationException("只有支持断点续传的任务才能整合。");
        }

        if (target.SegmentCount != redundant.SegmentCount)
        {
            throw new InvalidOperationException("两个任务的分段数量不同，当前版本不能安全整合。");
        }

        if (File.Exists(target.ResolvedTargetPath!) || File.Exists(redundant.ResolvedTargetPath!))
        {
            throw new InvalidOperationException("任务已经存在完整目标文件，不能再整合临时分块。");
        }
    }

    private static IReadOnlyList<SegmentRange> GetRanges(DownloadJob job)
    {
        if (job.TotalBytes is not > 0 || string.IsNullOrWhiteSpace(job.ResolvedTargetPath))
        {
            throw new InvalidOperationException("任务还没有可整合的分块信息。");
        }

        return RangePlanner.Plan(job.TotalBytes.Value, job.SegmentCount, 1);
    }

    private static string GetPartsDirectory(DownloadJob job) => job.ResolvedTargetPath! + ".bfdl.parts";

    private static string PartPath(string partsDirectory, int index) =>
        Path.Combine(partsDirectory, $"part-{index:D3}.tmp");

    private static long GetValidatedPartLength(string partsDirectory, SegmentRange range)
    {
        var path = PartPath(partsDirectory, range.Index);
        if (!File.Exists(path))
        {
            return 0;
        }

        var length = new FileInfo(path).Length;
        if (length > range.Length)
        {
            throw new InvalidOperationException($"分块 {range.Index + 1} 的长度超过规划范围，已停止整合。");
        }

        return length;
    }

    private static async Task EnsureContentsMatchAsync(
        string firstPath,
        string secondPath,
        long length,
        CancellationToken cancellationToken)
    {
        await using var first = OpenReadExclusivelyAgainstWriters(firstPath);
        await using var second = OpenReadExclusivelyAgainstWriters(secondPath);
        var firstBuffer = new byte[BufferSize];
        var secondBuffer = new byte[BufferSize];
        var remaining = length;

        while (remaining > 0)
        {
            var count = (int)Math.Min(BufferSize, remaining);
            await first.ReadExactlyAsync(firstBuffer.AsMemory(0, count), cancellationToken);
            await second.ReadExactlyAsync(secondBuffer.AsMemory(0, count), cancellationToken);
            if (!firstBuffer.AsSpan(0, count).SequenceEqual(secondBuffer.AsSpan(0, count)))
            {
                throw new InvalidOperationException("两个任务的已下载内容不同，已停止整合以避免文件损坏。");
            }

            remaining -= count;
        }
    }

    private static FileStream OpenReadExclusivelyAgainstWriters(string path) => new(
        path,
        FileMode.Open,
        FileAccess.Read,
        FileShare.Read,
        BufferSize,
        FileOptions.Asynchronous | FileOptions.SequentialScan);

    private static bool PathsEqual(string first, string second) => string.Equals(
        Path.GetFullPath(first),
        Path.GetFullPath(second),
        StringComparison.OrdinalIgnoreCase);
}
