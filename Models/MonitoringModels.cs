namespace Desktop.Models;

public sealed record MonitorSettings(
    string ServerUrl,
    int IntervalSeconds,
    int HeartbeatSeconds,
    string MachineId,
    string UploadKey,
    bool PrivacyMode,
    bool ForceAllowLongTitle,
    bool ReportMedia);

public sealed record ForegroundWindowSnapshot(string Title, string Application, int ProcessId);

/// <summary>当前系统媒体会话（SMTC）的快照。</summary>
public sealed record MediaPlaybackSnapshot(
    string Title,
    string Artist,
    string Album,
    string PlaybackStatus,
    string SourceApp)
{
    public bool HasContent =>
        !string.IsNullOrWhiteSpace(Title) || !string.IsNullOrWhiteSpace(Artist);

    public string Key => $"{Title}\u001f{Artist}\u001f{Album}\u001f{PlaybackStatus}\u001f{SourceApp}";

    public string DisplayText
    {
        get
        {
            var song = string.IsNullOrWhiteSpace(Artist)
                ? Title
                : string.IsNullOrWhiteSpace(Title) ? Artist : $"{Artist} - {Title}";
            var status = PlaybackStatus switch
            {
                "playing" => "播放中",
                "paused" => "已暂停",
                "stopped" => "已停止",
                "changing" => "切换中",
                "closed" => "已关闭",
                _ => PlaybackStatus
            };

            if (string.IsNullOrWhiteSpace(song))
            {
                return status;
            }

            return string.IsNullOrWhiteSpace(status) ? song : $"{song}（{status}）";
        }
    }
}

/// <summary>SMTC 读取结果的三种情形。读取失败不能当作“会话消失”，否则会误发 closed。</summary>
public enum MediaSessionReadStatus
{
    NoSession,
    Unavailable,
    Available
}

public sealed record MediaSessionReadResult(MediaSessionReadStatus Status, MediaPlaybackSnapshot? Snapshot)
{
    public static MediaSessionReadResult NoSession { get; } = new(MediaSessionReadStatus.NoSession, null);

    public static MediaSessionReadResult Unavailable { get; } = new(MediaSessionReadStatus.Unavailable, null);

    public static MediaSessionReadResult FromSnapshot(MediaPlaybackSnapshot snapshot) =>
        new(MediaSessionReadStatus.Available, snapshot);
}

public sealed class MonitoringStatusChangedEventArgs(bool isRunning) : EventArgs
{
    public bool IsRunning { get; } = isRunning;
}

public sealed class UsageSentEventArgs(DateTimeOffset sentAt, string application, string title, string? media = null) : EventArgs
{
    public DateTimeOffset SentAt { get; } = sentAt;
    public string Application { get; } = application;
    public string Title { get; } = title;
    public string? Media { get; } = media;
}

public sealed class MonitoringErrorEventArgs(
    string message,
    bool stopsMonitoring,
    int? statusCode = null,
    string? code = null) : EventArgs
{
    public string Message { get; } = message;
    public bool StopsMonitoring { get; } = stopsMonitoring;
    public int? StatusCode { get; } = statusCode;
    public string? Code { get; } = code;
}
