namespace Desktop.Models;

public sealed record MonitorSettings(
    string ServerUrl,
    int IntervalMs,
    int HeartbeatMs,
    string MachineId,
    string UploadKey,
    bool PrivacyMode,
    bool ForceAllowLongTitle,
    bool ReportMedia);

public sealed record ForegroundWindowSnapshot(string Title, string Application, int ProcessId);

/// <summary>当前系统媒体会话（SMTC）的快照。</summary>
/// <param name="Title">曲目标题。</param>
/// <param name="Artist">艺术家。</param>
/// <param name="Album">专辑。</param>
/// <param name="PlaybackStatus">播放状态文本。</param>
/// <param name="SourceApp">来源应用规范化短名。</param>
/// <param name="SourceName">来源应用的友好显示名（如“网易云音乐”）；未知时为空串。</param>
/// <param name="DurationMs">曲目总时长（毫秒）；无时间轴信息时为 null。</param>
/// <param name="PositionMs">采样时刻的播放位置（毫秒）；无时间轴信息时为 null。</param>
/// <param name="PositionCapturedAt">SMTC 上报该播放位置的时刻（UTC），用于计算位置新鲜度。</param>
/// <param name="PlaybackSpeed">播放速率；未知或非正时为 null。</param>
public sealed record MediaPlaybackSnapshot(
    string Title,
    string Artist,
    string Album,
    string PlaybackStatus,
    string SourceApp,
    string SourceName = "",
    long? DurationMs = null,
    long? PositionMs = null,
    DateTimeOffset? PositionCapturedAt = null,
    double? PlaybackSpeed = null)
{
    public bool HasContent =>
        !string.IsNullOrWhiteSpace(Title) || !string.IsNullOrWhiteSpace(Artist);

    // Key 只用于判断“媒体是否变化”，刻意不含持续变化的播放位置/进度，
    // 否则每次心跳位置都不同会误判为变化、反复补报。
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
