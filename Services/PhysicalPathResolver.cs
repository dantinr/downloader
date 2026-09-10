using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using BigFileDownloader.Models;
using Microsoft.Win32.SafeHandles;

namespace BigFileDownloader.Services;

internal static class PhysicalPathResolver
{
    private const uint ShareRead = 0x00000001;
    private const uint ShareWrite = 0x00000002;
    private const uint ShareDelete = 0x00000004;
    private const uint OpenExisting = 3;
    private const uint BackupSemantics = 0x02000000;

    public static string ResolveForComparison(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var unresolvedNames = new Stack<string>();
        var existingPath = fullPath;

        while (!Exists(existingPath))
        {
            var parent = Path.GetDirectoryName(existingPath);
            var name = Path.GetFileName(existingPath);
            if (string.IsNullOrWhiteSpace(parent) || string.IsNullOrWhiteSpace(name))
            {
                throw new IOException($"路径不存在或当前无法访问：{fullPath}");
            }

            unresolvedNames.Push(name);
            existingPath = parent;
        }

        var resolvedPath = ResolveExistingPath(existingPath);
        while (unresolvedNames.TryPop(out var name))
        {
            resolvedPath = Path.Combine(resolvedPath, name);
        }

        return resolvedPath;
    }

    public static TargetFileFingerprint GetFileFingerprint(string path)
    {
        using var handle = OpenPath(path);
        var information = GetPathInformation(handle, path);
        var length = checked((long)(((ulong)information.FileSizeHigh << 32) | information.FileSizeLow));
        var lastWriteTime = checked((long)(((ulong)information.LastWriteTime.High << 32) | information.LastWriteTime.Low));
        return new TargetFileFingerprint(
            FormatIdentity(information),
            length,
            lastWriteTime);
    }

    public static string GetFileSystemIdentity(string path)
    {
        using var handle = OpenPath(path);
        return FormatIdentity(GetPathInformation(handle, path));
    }

    public static bool MatchesFileFingerprint(string path, TargetFileFingerprint? expected)
    {
        if (expected is null)
        {
            return false;
        }

        try
        {
            return GetFileFingerprint(path) == expected;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            DiagnosticLog.Warning("Files", $"Target identity could not be verified; path={path}; error={exception.Message}");
            return false;
        }
    }

    private static bool Exists(string path)
    {
        try
        {
            _ = File.GetAttributes(path);
            return true;
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return false;
        }
    }

    private static string ResolveExistingPath(string path)
    {
        using var handle = OpenPath(path);

        var capacity = 512;
        while (true)
        {
            var buffer = new StringBuilder(capacity);
            var length = GetFinalPathNameByHandle(handle, buffer, (uint)buffer.Capacity, 0);
            if (length == 0)
            {
                throw new IOException(
                    $"无法验证路径的实际位置：{path}",
                    new Win32Exception(Marshal.GetLastWin32Error()));
            }

            if (length < buffer.Capacity)
            {
                return RemoveExtendedPathPrefix(buffer.ToString());
            }

            capacity = checked((int)length + 1);
        }
    }

    private static ByHandleFileInformation GetPathInformation(SafeFileHandle handle, string path)
    {
        if (!GetFileInformationByHandle(handle, out var information))
        {
            throw new IOException(
                $"无法验证文件身份：{path}",
                new Win32Exception(Marshal.GetLastWin32Error()));
        }

        return information;
    }

    private static string FormatIdentity(ByHandleFileInformation information)
    {
        var fileIndex = ((ulong)information.FileIndexHigh << 32) | information.FileIndexLow;
        return $"{information.VolumeSerialNumber:X8}:{fileIndex:X16}";
    }

    private static SafeFileHandle OpenPath(string path)
    {
        var handle = CreateFile(
            path,
            0,
            ShareRead | ShareWrite | ShareDelete,
            IntPtr.Zero,
            OpenExisting,
            BackupSemantics,
            IntPtr.Zero);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            handle.Dispose();
            throw new IOException(
                $"无法验证路径的实际位置：{path}",
                new Win32Exception(error));
        }

        return handle;
    }

    private static string RemoveExtendedPathPrefix(string path)
    {
        const string uncPrefix = @"\\?\UNC\";
        const string extendedPrefix = @"\\?\";
        if (path.StartsWith(uncPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return @"\\" + path[uncPrefix.Length..];
        }

        return path.StartsWith(extendedPrefix, StringComparison.OrdinalIgnoreCase)
            ? path[extendedPrefix.Length..]
            : path;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        IntPtr securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandle(
        SafeFileHandle file,
        [Out] StringBuilder filePath,
        uint filePathLength,
        uint flags);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(
        SafeFileHandle file,
        out ByHandleFileInformation fileInformation);

    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public FileTime CreationTime;
        public FileTime LastAccessTime;
        public FileTime LastWriteTime;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileTime
    {
        public uint Low;
        public uint High;
    }
}
