using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace DownloadManager;

internal enum DownloadState
{
    Queued,
    Downloading,
    Pausing,
    Paused,
    Cancelling,
    Cancelled,
    Completed,
    Failed
}

internal enum DownloadAction
{
    None,
    Pause,
    Cancel
}

internal sealed class DownloadJob : INotifyPropertyChanged
{
    private DownloadState _state = DownloadState.Queued;
    private long _bytesReceived;
    private long? _totalBytes;
    private string _details = string.Empty;
    private double _bytesPerSecond;
    private long _lastSampleBytes;
    private long _lastSampleTimestamp;

    public DownloadJob(string url, string destinationPath, Guid? id = null)
    {
        Id = id ?? Guid.NewGuid();
        Url = url;
        DestinationPath = destinationPath;
    }

    public Guid Id { get; }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Url { get; }

    public string DestinationPath { get; }

    public string FileName => Path.GetFileName(DestinationPath);

    public string Source => Uri.TryCreate(Url, UriKind.Absolute, out var uri) ? uri.Host : string.Empty;

    public string Category => GetCategory(Path.GetExtension(FileName));

    public DownloadState State => _state;

    public string StatusText => _state switch
    {
        DownloadState.Queued => "Queued",
        DownloadState.Downloading => "Downloading",
        DownloadState.Pausing => "Pausing",
        DownloadState.Paused => "Paused",
        DownloadState.Cancelling => "Stopping",
        DownloadState.Cancelled => "Cancelled",
        DownloadState.Completed => "Complete",
        DownloadState.Failed => "Failed",
        _ => _state.ToString()
    };

    public string SizeText => _totalBytes is long totalBytes
        ? FormatBytes(totalBytes)
        : "Unknown";

    public string RateText => _state == DownloadState.Downloading && _bytesPerSecond > 0
        ? $"{FormatBytes((long)_bytesPerSecond)}/s"
        : "-";

    public string TimeLeftText
    {
        get
        {
            if (_state != DownloadState.Downloading || _totalBytes is not > 0 || _bytesPerSecond <= 0)
            {
                return "-";
            }

            var secondsRemaining = Math.Max(0, (_totalBytes.Value - _bytesReceived) / _bytesPerSecond);
            var remaining = TimeSpan.FromSeconds(Math.Min(secondsRemaining, TimeSpan.MaxValue.TotalSeconds));
            if (remaining.TotalDays >= 1)
            {
                return $"{(int)remaining.TotalDays}d {remaining.Hours}h";
            }
            if (remaining.TotalHours >= 1)
            {
                return $"{(int)remaining.TotalHours}h {remaining.Minutes}m";
            }
            if (remaining.TotalMinutes >= 1)
            {
                return $"{(int)remaining.TotalMinutes}m {remaining.Seconds}s";
            }

            return $"{Math.Max(1, (int)Math.Ceiling(remaining.TotalSeconds))}s";
        }
    }

    public string ProgressText
    {
        get
        {
            if (_totalBytes is > 0)
            {
                var percent = Math.Min(100, _bytesReceived * 100.0 / _totalBytes.Value);
                return $"{percent:F1}% ({FormatBytes(_bytesReceived)} / {FormatBytes(_totalBytes.Value)})";
            }

            return _bytesReceived > 0 ? $"{FormatBytes(_bytesReceived)} received" : "Waiting";
        }
    }

    public string Details => _details;

    internal long BytesReceived => _bytesReceived;

    internal long? TotalBytes => _totalBytes;

    internal DownloadAction RequestedAction { get; set; }

    internal void Restore(
        DownloadState state,
        string details,
        long bytesReceived,
        long? totalBytes)
    {
        _details = details;
        _bytesReceived = Math.Max(0, bytesReceived);
        _totalBytes = totalBytes;
        _state = state;
    }

    internal void SetState(DownloadState state)
    {
        if (_state == state)
        {
            return;
        }

        _state = state;
        if (state == DownloadState.Downloading)
        {
            _bytesPerSecond = 0;
            _lastSampleTimestamp = 0;
        }
        OnPropertyChanged(nameof(State));
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(RateText));
        OnPropertyChanged(nameof(TimeLeftText));
    }

    internal void SetProgress(long bytesReceived, long? totalBytes)
    {
        var now = Stopwatch.GetTimestamp();
        if (_lastSampleTimestamp != 0)
        {
            var elapsed = Stopwatch.GetElapsedTime(_lastSampleTimestamp, now).TotalSeconds;
            if (elapsed > 0)
            {
                _bytesPerSecond = Math.Max(0, bytesReceived - _lastSampleBytes) / elapsed;
            }
        }
        _lastSampleBytes = bytesReceived;
        _lastSampleTimestamp = now;
        _bytesReceived = bytesReceived;
        _totalBytes = totalBytes;
        OnPropertyChanged(nameof(ProgressText));
        OnPropertyChanged(nameof(SizeText));
        OnPropertyChanged(nameof(RateText));
        OnPropertyChanged(nameof(TimeLeftText));
    }

    internal void SetDetails(string details)
    {
        if (_details == details)
        {
            return;
        }

        _details = details;
        OnPropertyChanged(nameof(Details));
    }

    private static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        var value = (double)bytes;
        var unitIndex = 0;

        while (value >= 1024 && unitIndex < units.Length - 1)
        {
            value /= 1024;
            unitIndex++;
        }

        return unitIndex == 0 ? $"{bytes} {units[unitIndex]}" : $"{value:F1} {units[unitIndex]}";
    }

    private static string GetCategory(string extension) => extension.ToLowerInvariant() switch
    {
        ".zip" or ".7z" or ".rar" or ".tar" or ".gz" or ".bz2" or ".xz" => "Compressed",
        ".pdf" or ".doc" or ".docx" or ".odt" or ".rtf" or ".txt" or ".xls" or ".xlsx" or ".ppt" or ".pptx" =>
            "Documents",
        ".mp3" or ".m4a" or ".aac" or ".flac" or ".ogg" or ".wav" or ".wma" => "Music",
        ".exe" or ".msi" or ".msix" or ".appx" or ".apk" or ".dmg" => "Programs",
        ".mp4" or ".m4v" or ".mkv" or ".mov" or ".avi" or ".webm" or ".wmv" or ".flv" => "Video",
        _ => "Other"
    };

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
