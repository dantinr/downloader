using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace BigFileDownloader.Services;

internal static class DiagnosticLog
{
    private static readonly object Gate = new();
    private static readonly Regex UrlQueryPattern = new(
        "(?<base>https?://[^\\s?\\\"'<>]+)\\?[^\\s\\\"'<>]+",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly string SessionId = Guid.NewGuid().ToString("N")[..8];
    private static StreamWriter? _writer;
    private static DateOnly? _writerDate;
    private static string? _currentLogPath;

    public static string LogsDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "downloader",
        "logs");

    public static string CurrentLogPath
    {
        get
        {
            lock (Gate)
            {
                EnsureWriter();
                return _currentLogPath ?? Path.Combine(LogsDirectory, $"downloader-{DateTime.Now:yyyyMMdd}.log");
            }
        }
    }

    public static void Initialize()
    {
        var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "unknown";
        Info(
            "App",
            $"Session started; session={SessionId}; version={version}; " +
            $"os={RuntimeInformation.OSDescription}; framework={RuntimeInformation.FrameworkDescription}; " +
            $"architecture={RuntimeInformation.ProcessArchitecture}");
    }

    public static void Info(string source, string message) => Write("INFO", source, message);

    public static void Warning(string source, string message) => Write("WARN", source, message);

    public static void Error(string source, string message, Exception exception) =>
        Write("ERROR", source, $"{message}{Environment.NewLine}{exception}");

    public static string SafeUrl(string? value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri))
        {
            return "<invalid-url>";
        }

        var builder = new UriBuilder(uri)
        {
            Query = string.Empty,
            Fragment = string.Empty
        };
        return builder.Uri.AbsoluteUri;
    }

    public static void Shutdown()
    {
        Info("App", $"Session ended; session={SessionId}");
        lock (Gate)
        {
            _writer?.Dispose();
            _writer = null;
        }
    }

    private static void Write(string level, string source, string message)
    {
        lock (Gate)
        {
            try
            {
                EnsureWriter();
                if (_writer is null)
                {
                    return;
                }

                var sanitized = Redact(message).Replace(
                    Environment.NewLine,
                    Environment.NewLine + "    ",
                    StringComparison.Ordinal);
                _writer.WriteLine(
                    $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz} [{level}] " +
                    $"[P:{Environment.ProcessId} T:{Environment.CurrentManagedThreadId}] [{source}] {sanitized}");
            }
            catch
            {
                try
                {
                    _writer?.Dispose();
                }
                catch
                {
                }

                _writer = null;
            }
        }
    }

    private static void EnsureWriter()
    {
        var today = DateOnly.FromDateTime(DateTime.Now);
        if (_writer is not null && _writerDate == today)
        {
            return;
        }

        _writer?.Dispose();
        Directory.CreateDirectory(LogsDirectory);
        _currentLogPath = Path.Combine(LogsDirectory, $"downloader-{today:yyyyMMdd}.log");
        var stream = new FileStream(
            _currentLogPath,
            FileMode.Append,
            FileAccess.Write,
            FileShare.ReadWrite,
            16 * 1024,
            FileOptions.SequentialScan);
        _writer = new StreamWriter(stream, new UTF8Encoding(false))
        {
            AutoFlush = true
        };
        _writerDate = today;
    }

    private static string Redact(string message) => UrlQueryPattern.Replace(
        message,
        match => match.Groups["base"].Value + "?<redacted>");
}
