using BigFileDownloader.Models;

namespace BigFileDownloader.Services;

internal sealed record DownloadArtifactPlan(
    string TargetPath,
    string PartsDirectory,
    string TemporaryPath,
    string AssemblingPath,
    string ComparisonTargetPath,
    string ComparisonPartsDirectory,
    string ComparisonTemporaryPath,
    string ComparisonAssemblingPath,
    string ComparisonDirectoryIdentity)
{
    private static readonly StringComparer PathComparer = StringComparer.Ordinal;
    private static readonly StringComparer ConflictPathComparer = StringComparer.OrdinalIgnoreCase;

    public IReadOnlyList<string> ProtectedPaths =>
        [TargetPath, PartsDirectory, TemporaryPath, AssemblingPath];

    public string DeletionTargetPath => ComparisonTargetPath;

    public string DeletionPartsDirectory => ComparisonPartsDirectory;

    public string DeletionTemporaryPath => ComparisonTemporaryPath;

    public string DeletionAssemblingPath => ComparisonAssemblingPath;

    private IReadOnlyList<string> ComparisonProtectedPaths =>
        [
            DeletionTargetPath,
            DeletionPartsDirectory,
            DeletionTemporaryPath,
            DeletionAssemblingPath
        ];

    public static DownloadArtifactPlan? CreateFor(DownloadJob job)
    {
        ArgumentNullException.ThrowIfNull(job);
        return CreateFor(job.DestinationFolder, job.FileName, job.ResolvedTargetPath);
    }

    public static DownloadArtifactPlan? CreateFor(
        string destinationFolder,
        string fileName,
        string? resolvedTargetPath)
    {
        if (string.IsNullOrWhiteSpace(resolvedTargetPath))
        {
            return null;
        }

        if (!Path.IsPathFullyQualified(destinationFolder))
        {
            throw new InvalidOperationException(
                "任务文件路径不是绝对路径。为避免误删，只能删除任务记录。");
        }

        var destination = Path.TrimEndingDirectorySeparator(Path.GetFullPath(destinationFolder));
        var plan = CreateForTargetPath(resolvedTargetPath);
        var targetDirectory = Path.GetDirectoryName(plan.TargetPath);
        if (string.IsNullOrWhiteSpace(targetDirectory)
            || !PathComparer.Equals(Path.TrimEndingDirectorySeparator(targetDirectory), destination))
        {
            throw new InvalidOperationException(
                "任务文件不在记录的保存文件夹中。为避免误删，只能删除任务记录。");
        }

        var targetFileName = Path.GetFileName(plan.TargetPath);
        if (string.IsNullOrWhiteSpace(targetFileName)
            || (!string.IsNullOrWhiteSpace(fileName)
                && !PathComparer.Equals(targetFileName, fileName)))
        {
            throw new InvalidOperationException(
                "任务文件名与磁盘路径不一致。为避免误删，只能删除任务记录。");
        }

        return plan;
    }

    public static DownloadArtifactPlan CreateForTargetPath(string targetPath)
    {
        if (string.IsNullOrWhiteSpace(targetPath) || !Path.IsPathFullyQualified(targetPath))
        {
            throw new InvalidOperationException("任务文件路径不是绝对路径。");
        }

        var targetFileName = Path.GetFileName(targetPath);
        var targetDirectory = Path.GetDirectoryName(targetPath);
        if (string.IsNullOrWhiteSpace(targetFileName) || string.IsNullOrWhiteSpace(targetDirectory))
        {
            throw new InvalidOperationException("任务目标路径不是文件路径。");
        }

        var normalizedDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(targetDirectory));
        var target = Path.Combine(normalizedDirectory, targetFileName);
        var root = Path.GetPathRoot(target) ?? string.Empty;

        if (target.AsSpan(root.Length).Contains(':'))
        {
            throw new InvalidOperationException(
                "任务文件路径包含不受支持的数据流。为避免误删，只能删除任务记录。");
        }

        var partsDirectory = target + ".bfdl.parts";
        var temporaryPath = target + ".bfdl.tmp";
        var assemblingPath = target + ".assembling";
        EnsureLeafIsNotReparsePoint(target);
        EnsureLeafIsNotReparsePoint(partsDirectory);
        EnsureLeafIsNotReparsePoint(temporaryPath);
        EnsureLeafIsNotReparsePoint(assemblingPath);

        var comparisonTarget = PhysicalPathResolver.ResolveForComparison(target);
        var comparisonDirectory = Path.GetDirectoryName(comparisonTarget)
            ?? throw new InvalidOperationException("任务目标路径缺少保存文件夹。");

        return new DownloadArtifactPlan(
            target,
            partsDirectory,
            temporaryPath,
            assemblingPath,
            comparisonTarget,
            PhysicalPathResolver.ResolveForComparison(partsDirectory),
            PhysicalPathResolver.ResolveForComparison(temporaryPath),
            PhysicalPathResolver.ResolveForComparison(assemblingPath),
            PhysicalPathResolver.GetFileSystemIdentity(comparisonDirectory));
    }

    private static void EnsureLeafIsNotReparsePoint(string path)
    {
        FileAttributes attributes;
        try
        {
            attributes = File.GetAttributes(path);
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return;
        }

        if ((attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new IOException($"任务文件或临时数据是文件链接，为避免误删已停止操作：{path}");
        }
    }

    public bool HasSameTarget(DownloadArtifactPlan other) =>
        PathComparer.Equals(ComparisonTargetPath, other.ComparisonTargetPath)
        && string.Equals(ComparisonDirectoryIdentity, other.ComparisonDirectoryIdentity, StringComparison.Ordinal);

    public bool HasSameArtifacts(DownloadArtifactPlan other) =>
        HasSameTarget(other)
        && PathComparer.Equals(ComparisonPartsDirectory, other.ComparisonPartsDirectory)
        && PathComparer.Equals(ComparisonTemporaryPath, other.ComparisonTemporaryPath)
        && PathComparer.Equals(ComparisonAssemblingPath, other.ComparisonAssemblingPath);

    public string ArtifactIdentityKey => string.Join(
        '\0',
        ComparisonDirectoryIdentity,
        ComparisonTargetPath,
        ComparisonPartsDirectory,
        ComparisonTemporaryPath,
        ComparisonAssemblingPath);

    public bool ConflictsWithPendingDestination(string comparisonDirectory)
    {
        var directory = Path.TrimEndingDirectorySeparator(comparisonDirectory);
        var targetDirectory = Path.GetDirectoryName(ComparisonTargetPath);
        return (targetDirectory is not null && ConflictPathComparer.Equals(directory, targetDirectory))
            || IsSameOrDescendant(directory, ComparisonPartsDirectory);
    }

    public bool Overlaps(DownloadArtifactPlan other)
    {
        if (ComparisonProtectedPaths.Any(
            path => other.ComparisonProtectedPaths.Contains(path, ConflictPathComparer)))
        {
            return true;
        }

        var otherPartsDirectory = other.DeletionPartsDirectory;
        var partsDirectory = DeletionPartsDirectory;
        return ComparisonProtectedPaths.Any(path => IsSameOrDescendant(path, otherPartsDirectory))
            || other.ComparisonProtectedPaths.Any(path => IsSameOrDescendant(path, partsDirectory));
    }

    private static bool IsSameOrDescendant(string path, string directory)
    {
        if (ConflictPathComparer.Equals(path, directory))
        {
            return true;
        }

        var prefix = Path.TrimEndingDirectorySeparator(directory) + Path.DirectorySeparatorChar;
        return path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }
}
