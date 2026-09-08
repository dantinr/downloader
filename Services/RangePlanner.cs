namespace BigFileDownloader.Services;

internal readonly record struct SegmentRange(int Index, long Start, long End)
{
    public long Length => End - Start + 1;
}

internal static class RangePlanner
{
    public static IReadOnlyList<SegmentRange> Plan(long totalBytes, int desiredSegments, long minimumSegmentSize)
    {
        if (totalBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(totalBytes));
        }

        if (minimumSegmentSize <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(minimumSegmentSize));
        }

        var maximumUsefulSegments = Math.Max(1, (int)Math.Min(16, totalBytes / minimumSegmentSize));
        var segmentCount = Math.Clamp(desiredSegments, 1, maximumUsefulSegments);
        var baseLength = totalBytes / segmentCount;
        var remainder = totalBytes % segmentCount;
        var ranges = new List<SegmentRange>(segmentCount);
        long cursor = 0;

        for (var index = 0; index < segmentCount; index++)
        {
            var length = baseLength + (index < remainder ? 1 : 0);
            ranges.Add(new SegmentRange(index, cursor, cursor + length - 1));
            cursor += length;
        }

        return ranges;
    }
}
