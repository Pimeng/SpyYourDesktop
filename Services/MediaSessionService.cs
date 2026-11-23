using Desktop.Infrastructure;
using Desktop.Models;
using Windows.Media.Control;

namespace Desktop.Services;

public interface IMediaSessionService
{
    /// <summary>
    /// 读取当前系统媒体会话（SMTC）。没有会话返回 <see cref="MediaSessionReadResult.NoSession"/>，
    /// 读取失败返回 <see cref="MediaSessionReadResult.Unavailable"/>。
    /// </summary>
    Task<MediaSessionReadResult> ReadCurrentAsync(CancellationToken cancellationToken);
}

/// <summary>
/// 通过 Windows SMTC（GlobalSystemMediaTransportControls）读取当前正在播放的媒体信息。
/// 该 API 面向 Win32 桌面应用可用，不需要打包身份或额外清单能力。
/// </summary>
public sealed class MediaSessionService(IAppLogger logger) : IMediaSessionService
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private GlobalSystemMediaTransportControlsSessionManager? _manager;
    private bool _failureLogged;

    /// <summary>
    /// 常见播放器 AUMID / 进程名片段到友好显示名的映射。按 AUMID（含包名/进程名）小写子串匹配，
    /// 对桌面版与商店打包版都尽量命中；未命中返回空串，由读取端回退到 <c>source</c>。
    /// </summary>
    private static readonly (string Token, string Name)[] KnownSources =
    [
        ("cloudmusic", "网易云音乐"),
        ("neteasecloudmusic", "网易云音乐"),
        ("qqmusic", "QQ音乐"),
        ("kugou", "酷狗音乐"),
        ("kwmusic", "酷我音乐"),
        ("kuwo", "酷我音乐"),
        ("migu", "咪咕音乐"),
        ("spotify", "Spotify"),
        ("foobar2000", "foobar2000"),
        ("aimp", "AIMP"),
        ("potplayer", "PotPlayer"),
        ("zunemusic", "Groove 音乐"),
        ("applemusic", "Apple Music"),
        ("itunes", "iTunes"),
        ("bilibili", "哔哩哔哩"),
        ("iqiyi", "爱奇艺"),
        ("tencentvideo", "腾讯视频"),
        ("youku", "优酷"),
        ("msedge", "Microsoft Edge"),
        ("chrome", "Google Chrome"),
        ("firefox", "Mozilla Firefox"),
    ];

    public async Task<MediaSessionReadResult> ReadCurrentAsync(CancellationToken cancellationToken)
    {
        try
        {
            var manager = await GetManagerAsync(cancellationToken);
            if (manager is null)
            {
                return MediaSessionReadResult.Unavailable;
            }

            var session = manager.GetCurrentSession();
            if (session is null)
            {
                return MediaSessionReadResult.NoSession;
            }

            var properties = await session.TryGetMediaPropertiesAsync().AsTask(cancellationToken);
            var playbackInfo = session.GetPlaybackInfo();
            var status = playbackInfo?.PlaybackStatus;

            ReadTimeline(session.GetTimelineProperties(), out var durationMs, out var positionMs, out var positionCapturedAt);

            var snapshot = new MediaPlaybackSnapshot(
                Truncate(properties?.Title),
                Truncate(properties?.Artist),
                Truncate(properties?.AlbumTitle),
                ToStatusText(status),
                NormalizeSourceApp(session.SourceAppUserModelId),
                ResolveSourceName(session.SourceAppUserModelId),
                durationMs,
                positionMs,
                positionCapturedAt,
                NormalizeSpeed(playbackInfo?.PlaybackRate));

            return snapshot.HasContent
                ? MediaSessionReadResult.FromSnapshot(snapshot)
                : MediaSessionReadResult.NoSession;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            if (!_failureLogged)
            {
                _failureLogged = true;
                await logger.LogAsync($"[media] 无法读取系统媒体会话：{exception.Message}", cancellationToken);
            }

            return MediaSessionReadResult.Unavailable;
        }
    }

    private async Task<GlobalSystemMediaTransportControlsSessionManager?> GetManagerAsync(CancellationToken cancellationToken)
    {
        var cached = Volatile.Read(ref _manager);
        if (cached is not null)
        {
            return cached;
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_manager is not null)
            {
                return _manager;
            }

            _manager = await GlobalSystemMediaTransportControlsSessionManager
                .RequestAsync()
                .AsTask(cancellationToken);
            _failureLogged = false;
            return _manager;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// 从 SMTC 时间轴读取总时长与播放位置。没有时间轴信息（LastUpdatedTime 为默认值）时全部返回 null，
    /// 以免把“未知进度”误报成 0。位置以 <see cref="TimeSpan"/> 的 StartTime 为基准归一化。
    /// </summary>
    private static void ReadTimeline(
        GlobalSystemMediaTransportControlsSessionTimelineProperties? timeline,
        out long? durationMs,
        out long? positionMs,
        out DateTimeOffset? capturedAt)
    {
        durationMs = null;
        positionMs = null;
        capturedAt = null;

        if (timeline is null)
        {
            return;
        }

        // 应用未提供时间轴时 LastUpdatedTime 为默认值（纪元/MinValue）。
        var updatedAt = timeline.LastUpdatedTime;
        if (updatedAt <= DateTimeOffset.UnixEpoch)
        {
            return;
        }

        var start = timeline.StartTime;
        var position = timeline.Position - start;
        var duration = timeline.EndTime - start;

        if (duration > TimeSpan.Zero)
        {
            durationMs = (long)duration.TotalMilliseconds;
            if (position < TimeSpan.Zero)
            {
                position = TimeSpan.Zero;
            }
            else if (position > duration)
            {
                position = duration;
            }
        }
        else if (position < TimeSpan.Zero)
        {
            position = TimeSpan.Zero;
        }

        positionMs = (long)position.TotalMilliseconds;
        capturedAt = updatedAt.ToUniversalTime();
    }

    /// <summary>规整播放速率：仅在取到正数时上报，其余（null / 0 / 负数）返回 null，由读取端按 1 处理。</summary>
    private static double? NormalizeSpeed(double? rate) =>
        rate is { } value && value > 0 ? Math.Round(value, 3) : null;

    private static string ToStatusText(GlobalSystemMediaTransportControlsSessionPlaybackStatus? status) =>
        status switch
        {
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing => "playing",
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused => "paused",
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Stopped => "stopped",
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Changing => "changing",
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Closed => "closed",
            _ => string.Empty
        };

    /// <summary>
    /// 把来源 AUMID 匹配到已知播放器的友好显示名。命中返回中文/品牌名，未命中返回空串。
    /// </summary>
    private static string ResolveSourceName(string? aumid)
    {
        if (string.IsNullOrWhiteSpace(aumid))
        {
            return string.Empty;
        }

        var haystack = aumid.ToLowerInvariant();
        foreach (var (token, name) in KnownSources)
        {
            if (haystack.Contains(token, StringComparison.Ordinal))
            {
                return name;
            }
        }

        return string.Empty;
    }

    private static string NormalizeSourceApp(string? aumid)
    {
        if (string.IsNullOrWhiteSpace(aumid))
        {
            return string.Empty;
        }

        var value = aumid.Trim();
        var bangIndex = value.IndexOf('!');
        if (bangIndex > 0)
        {
            value = value[..bangIndex];
        }

        var fileName = Path.GetFileName(value);
        if (fileName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            fileName = fileName[..^4];
        }

        return fileName.Length > 64 ? fileName[..64] : fileName;
    }

    private static string Truncate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var result = value.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return result.Length > 256 ? result[..256] : result;
    }
}
