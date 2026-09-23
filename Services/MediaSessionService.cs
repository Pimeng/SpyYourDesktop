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
            var status = session.GetPlaybackInfo()?.PlaybackStatus;

            var snapshot = new MediaPlaybackSnapshot(
                Truncate(properties?.Title),
                Truncate(properties?.Artist),
                Truncate(properties?.AlbumTitle),
                ToStatusText(status),
                NormalizeSourceApp(session.SourceAppUserModelId));

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
