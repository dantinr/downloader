using System.Net.Http.Headers;

namespace BigFileDownloader.Services;

internal static class FileNameHelper
{
    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
    };

    public static string FromResponse(ContentDispositionHeaderValue? disposition, Uri uri)
    {
        var candidate = disposition?.FileNameStar ?? disposition?.FileName;
        candidate = candidate?.Trim().Trim('"');

        if (string.IsNullOrWhiteSpace(candidate))
        {
            candidate = Uri.UnescapeDataString(uri.Segments.LastOrDefault() ?? string.Empty).Trim('/');
        }

        if (string.IsNullOrWhiteSpace(candidate))
        {
            candidate = $"download-{DateTime.Now:yyyyMMdd-HHmmss}.bin";
        }

        return Sanitize(candidate);
    }

    public static string FromUrl(string url)
    {
        return Uri.TryCreate(url, UriKind.Absolute, out var uri)
            ? FromResponse(null, uri)
            : "新下载任务";
    }

    public static string Sanitize(string fileName)
    {
        var invalid = Path.GetInvalidFileNameChars().ToHashSet();
        var cleaned = new string(fileName.Select(character => invalid.Contains(character) ? '_' : character).ToArray());
        cleaned = cleaned.Trim().TrimEnd('.');

        if (string.IsNullOrWhiteSpace(cleaned))
        {
            cleaned = $"download-{DateTime.Now:yyyyMMdd-HHmmss}.bin";
        }

        var extension = Path.GetExtension(cleaned);
        var stem = Path.GetFileNameWithoutExtension(cleaned);
        if (ReservedNames.Contains(stem))
        {
            stem = $"_{stem}";
        }

        var maximumStemLength = Math.Max(1, 180 - extension.Length);
        if (stem.Length > maximumStemLength)
        {
            stem = stem[..maximumStemLength];
        }

        return stem + extension;
    }

    public static string EnsureUniquePath(string directory, string fileName)
    {
        var target = Path.Combine(directory, Sanitize(fileName));
        if (!File.Exists(target) && !Directory.Exists(target + ".bfdl.parts") && !File.Exists(target + ".bfdl.tmp"))
        {
            return target;
        }

        var stem = Path.GetFileNameWithoutExtension(fileName);
        var extension = Path.GetExtension(fileName);
        for (var index = 1; index < 10_000; index++)
        {
            target = Path.Combine(directory, $"{stem} ({index}){extension}");
            if (!File.Exists(target) && !Directory.Exists(target + ".bfdl.parts") && !File.Exists(target + ".bfdl.tmp"))
            {
                return target;
            }
        }

        throw new IOException("无法为下载文件生成唯一名称。");
    }
}
