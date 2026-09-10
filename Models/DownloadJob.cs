using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

namespace BigFileDownloader.Models;

public sealed class DownloadJob : INotifyPropertyChanged
{
    private string _url = string.Empty;
    private string _fileName = string.Empty;
    private string _destinationFolder = string.Empty;
    private string? _resolvedTargetPath;
    private long? _totalBytes;
    private long _downloadedBytes;
    private double _bytesPerSecond;
    private int _segmentCount = 4;
    private bool _supportsRanges;
    private string? _etag;
    private DateTimeOffset? _lastModified;
    private TargetFileOwnership _targetFileOwnership;
    private TargetFileFingerprint? _targetFileFingerprint;
    private DownloadState _state = DownloadState.Pending;
    private string _message = "等待开始";

    public Guid Id { get; set; } = Guid.NewGuid();
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;

    public string Url
    {
        get => _url;
        set => SetField(ref _url, value);
    }

    public string FileName
    {
        get => _fileName;
        set
        {
            if (SetField(ref _fileName, value))
            {
                OnPropertyChanged(nameof(TargetDisplay));
            }
        }
    }

    public string DestinationFolder
    {
        get => _destinationFolder;
        set
        {
            if (SetField(ref _destinationFolder, value))
            {
                OnPropertyChanged(nameof(TargetDisplay));
            }
        }
    }

    public string? ResolvedTargetPath
    {
        get => _resolvedTargetPath;
        set => SetField(ref _resolvedTargetPath, value);
    }

    public long? TotalBytes
    {
        get => _totalBytes;
        set
        {
            if (SetField(ref _totalBytes, value))
            {
                NotifyProgressProperties();
            }
        }
    }

    public long DownloadedBytes
    {
        get => _downloadedBytes;
        set
        {
            if (SetField(ref _downloadedBytes, value))
            {
                NotifyProgressProperties();
            }
        }
    }

    [JsonIgnore]
    public double BytesPerSecond
    {
        get => _bytesPerSecond;
        set
        {
            if (SetField(ref _bytesPerSecond, value))
            {
                OnPropertyChanged(nameof(SpeedDisplay));
                OnPropertyChanged(nameof(EtaDisplay));
            }
        }
    }

    public int SegmentCount
    {
        get => _segmentCount;
        set => SetField(ref _segmentCount, Math.Clamp(value, 1, 16));
    }

    public bool SupportsRanges
    {
        get => _supportsRanges;
        set => SetField(ref _supportsRanges, value);
    }

    public string? ETag
    {
        get => _etag;
        set => SetField(ref _etag, value);
    }

    public DateTimeOffset? LastModified
    {
        get => _lastModified;
        set => SetField(ref _lastModified, value);
    }

    public TargetFileOwnership TargetFileOwnership
    {
        get => _targetFileOwnership;
        set => SetField(ref _targetFileOwnership, value);
    }

    public TargetFileFingerprint? TargetFileFingerprint
    {
        get => _targetFileFingerprint;
        set => SetField(ref _targetFileFingerprint, value);
    }

    public DownloadState State
    {
        get => _state;
        set
        {
            if (SetField(ref _state, value))
            {
                OnPropertyChanged(nameof(StateDisplay));
                OnPropertyChanged(nameof(IsIndeterminate));
            }
        }
    }

    public string Message
    {
        get => _message;
        set => SetField(ref _message, value);
    }

    [JsonIgnore]
    public CancellationTokenSource? Cancellation { get; set; }

    [JsonIgnore]
    public Task? ActiveTask { get; set; }

    [JsonIgnore]
    public double ProgressPercent => TotalBytes is > 0
        ? Math.Clamp(DownloadedBytes * 100d / TotalBytes.Value, 0d, 100d)
        : 0d;

    [JsonIgnore]
    public bool IsIndeterminate => State == DownloadState.Downloading && TotalBytes is null or <= 0;

    [JsonIgnore]
    public string ProgressDisplay => TotalBytes is > 0
        ? $"{ProgressPercent:0.0}%"
        : State == DownloadState.Downloading ? "下载中" : "--";

    [JsonIgnore]
    public string SizeDisplay => TotalBytes is > 0 ? FormatBytes(TotalBytes.Value) : "未知";

    [JsonIgnore]
    public string DownloadedDisplay => FormatBytes(DownloadedBytes);

    [JsonIgnore]
    public string SpeedDisplay => BytesPerSecond > 0 ? $"{FormatBytes((long)BytesPerSecond)}/s" : "--";

    [JsonIgnore]
    public string EtaDisplay
    {
        get
        {
            if (BytesPerSecond <= 0 || TotalBytes is not > 0 || DownloadedBytes >= TotalBytes.Value)
            {
                return "--";
            }

            var seconds = (TotalBytes.Value - DownloadedBytes) / BytesPerSecond;
            if (double.IsNaN(seconds) || double.IsInfinity(seconds))
            {
                return "--";
            }

            var eta = TimeSpan.FromSeconds(Math.Min(seconds, TimeSpan.FromDays(99).TotalSeconds));
            return eta.TotalHours >= 1
                ? $"{(int)eta.TotalHours}:{eta.Minutes:00}:{eta.Seconds:00}"
                : $"{eta.Minutes:00}:{eta.Seconds:00}";
        }
    }

    [JsonIgnore]
    public string StateDisplay => State switch
    {
        DownloadState.Pending => "等待中",
        DownloadState.Inspecting => "正在连接",
        DownloadState.Downloading => "下载中",
        DownloadState.Pausing => "正在暂停",
        DownloadState.Paused => "已暂停",
        DownloadState.Merging => "正在合并",
        DownloadState.Deleting => "正在删除",
        DownloadState.DeletionFailed => "删除未完成",
        DownloadState.Completed => "已完成",
        DownloadState.Failed => "失败",
        _ => State.ToString()
    };

    [JsonIgnore]
    public string TargetDisplay => string.IsNullOrWhiteSpace(FileName)
        ? DestinationFolder
        : Path.Combine(DestinationFolder, FileName);

    public event PropertyChangedEventHandler? PropertyChanged;

    public void ApplyProgress(DownloadProgress progress)
    {
        if (progress.TotalBytes is > 0 && TotalBytes != progress.TotalBytes)
        {
            TotalBytes = progress.TotalBytes;
        }

        DownloadedBytes = progress.DownloadedBytes;
        BytesPerSecond = progress.BytesPerSecond;
    }

    public static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        var value = Math.Max(0, bytes);
        var display = (double)value;
        var unit = 0;
        while (display >= 1024 && unit < units.Length - 1)
        {
            display /= 1024;
            unit++;
        }

        return unit == 0 ? $"{value} B" : $"{display:0.##} {units[unit]}";
    }

    private void NotifyProgressProperties()
    {
        OnPropertyChanged(nameof(ProgressPercent));
        OnPropertyChanged(nameof(ProgressDisplay));
        OnPropertyChanged(nameof(SizeDisplay));
        OnPropertyChanged(nameof(DownloadedDisplay));
        OnPropertyChanged(nameof(EtaDisplay));
        OnPropertyChanged(nameof(IsIndeterminate));
    }

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
