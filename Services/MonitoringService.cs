using Desktop.Infrastructure;
using Desktop.Models;

namespace Desktop.Services;

public interface IMonitoringService : IAsyncDisposable
{
    bool IsRunning { get; }
    event EventHandler<MonitoringStatusChangedEventArgs>? StatusChanged;
    event EventHandler<UsageSentEventArgs>? UsageSent;
    event EventHandler<MonitoringErrorEventArgs>? Error;
    Task StartAsync(MonitorSettings settings, CancellationToken applicationCancellation);
    Task StopAsync();
    void UpdateRuntimeSettings(int intervalMs, int heartbeatMs, bool forceAllowLongTitle, bool reportMedia);
    Task SendCurrentAsync(bool privacyMode, CancellationToken cancellationToken);
}

public sealed class MonitoringService(
    IForegroundWindowService foregroundWindow,
    IMediaSessionService mediaSession,
    IUsageIngestService ingest,
    IAppLogger logger) : IMonitoringService
{
    private const int MaxServerErrorRetries = 3;
    private const int DefaultTitleLimit = 150;
    private const int TitleTruncateMargin = 10;
    private static readonly TimeSpan MediaIdleTimeout =
        TimeSpan.FromMilliseconds(IngestProtocol.DefaultMediaIdleTimeoutMs);
    private readonly object _stateLock = new();
    private readonly SemaphoreSlim _tickLock = new(1, 1);
    private readonly HashSet<string> _unsupportedEventTypes = new(StringComparer.Ordinal);
    private CancellationTokenSource? _monitorCancellation;
    private Task? _loopTask;
    private MonitorSettings? _settings;
    private string? _lastTitle;
    private MediaPlaybackSnapshot? _lastMediaSnapshot;
    private string? _lastMediaKey;
    private string? _mediaSessionIdentity;
    private DateTimeOffset? _mediaNonPlayingSince;
    private bool _mediaCloseSent;
    private DateTimeOffset _lastSentAt = DateTimeOffset.MinValue;
    private IngestSessionInfo? _session;
    private long _sequence;
    private int? _serverTitleLimit;
    private TimeSpan _serverPacing = TimeSpan.Zero;
    private bool _isRunning;
    private bool _disposed;

    public bool IsRunning
    {
        get
        {
            lock (_stateLock)
            {
                return _isRunning;
            }
        }
    }

    public event EventHandler<MonitoringStatusChangedEventArgs>? StatusChanged;
    public event EventHandler<UsageSentEventArgs>? UsageSent;
    public event EventHandler<MonitoringErrorEventArgs>? Error;

    public async Task StartAsync(MonitorSettings settings, CancellationToken applicationCancellation)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await StopAsync();

        if (!Uri.TryCreate(settings.ServerUrl, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            throw new ArgumentException("The ingest URL must use HTTP or HTTPS.", nameof(settings));
        }

        _monitorCancellation = CancellationTokenSource.CreateLinkedTokenSource(applicationCancellation);
        var token = _monitorCancellation.Token;

        lock (_stateLock)
        {
            _settings = settings;
            _lastTitle = null;
            ResetMediaTracking();
            _lastSentAt = DateTimeOffset.MinValue;
            _session = new IngestSessionInfo
            {
                Id = Guid.NewGuid().ToString("N"),
                StartedAt = ProtocolTime.UtcNow()
            };
            _sequence = 0;
            _serverTitleLimit = null;
            _serverPacing = TimeSpan.Zero;
            _unsupportedEventTypes.Clear();
            _isRunning = true;
        }

        StatusChanged?.Invoke(this, new MonitoringStatusChangedEventArgs(true));
        await TickAsync(token, IngestProtocol.Triggers.Startup);
        if (!token.IsCancellationRequested && IsRunning)
        {
            _loopTask = RunLoopAsync(token);
        }
    }

    public async Task StopAsync()
    {
        var cancellation = _monitorCancellation;
        cancellation?.Cancel();
        var loop = _loopTask;
        if (loop is not null && Task.CurrentId != loop.Id)
        {
            try
            {
                await loop;
            }
            catch (OperationCanceledException)
            {
            }
        }

        // SendCurrentAsync can run outside the loop; wait for the shared gate before disposing it.
        await _tickLock.WaitAsync();
        _tickLock.Release();

        _loopTask = null;
        _monitorCancellation = null;
        cancellation?.Dispose();
        SetRunning(false);
    }

    public async Task SendCurrentAsync(bool privacyMode, CancellationToken cancellationToken)
    {
        MonitorSettings? current;
        CancellationToken monitorToken;
        lock (_stateLock)
        {
            current = _settings;
            monitorToken = _monitorCancellation?.Token ?? cancellationToken;
            if (current is not null)
            {
                _settings = current with { PrivacyMode = privacyMode };
            }
        }

        if (current is not null && IsRunning)
        {
            using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, monitorToken);
            await TickAsync(linkedCancellation.Token, IngestProtocol.Triggers.Manual);
        }
    }

    public void UpdateRuntimeSettings(int intervalMs, int heartbeatMs, bool forceAllowLongTitle, bool reportMedia)
    {
        lock (_stateLock)
        {
            if (_settings is null)
            {
                return;
            }

            _settings = _settings with
            {
                IntervalMs = Math.Clamp(intervalMs, 0, 3600000),
                HeartbeatMs = Math.Clamp(heartbeatMs, 0, 3600000),
                ForceAllowLongTitle = forceAllowLongTitle,
                ReportMedia = reportMedia
            };
        }
    }

    private async Task RunLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var interval = GetInterval();
                await Task.Delay(interval, cancellationToken);
                await TickAsync(cancellationToken, forceTrigger: null);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        finally
        {
            SetRunning(false);
        }
    }

    private TimeSpan GetInterval()
    {
        lock (_stateLock)
        {
            var intervalMs = Math.Clamp(_settings?.IntervalMs ?? 1000, 0, 3600000);
            var interval = TimeSpan.FromMilliseconds(Math.Max(intervalMs, 1));
            // 服务端可以通过 pacing.next_upload_after_ms 主动限速。
            return _serverPacing > interval ? _serverPacing : interval;
        }
    }

    private TimeSpan GetHeartbeat()
    {
        lock (_stateLock)
        {
            return TimeSpan.FromMilliseconds(Math.Clamp(_settings?.HeartbeatMs ?? 5000, 0, 3600000));
        }
    }

    private async Task TickAsync(CancellationToken cancellationToken, string? forceTrigger)
    {
        await _tickLock.WaitAsync(cancellationToken);
        try
        {
            MonitorSettings? settings;
            IngestSessionInfo? session;
            lock (_stateLock)
            {
                settings = _settings;
                session = _session;
            }

            if (settings is null || session is null || !IsRunning)
            {
                return;
            }

            var snapshot = settings.PrivacyMode
                ? new ForegroundWindowSnapshot("TA现在不想给你看QAQ", "private mode", 0)
                : foregroundWindow.ReadCurrent();
            var title = ApplyTitlePolicy(snapshot.Title, settings);

            MediaPlaybackSnapshot? media = null;
            var mediaTracked = settings.ReportMedia && !settings.PrivacyMode;
            var mediaChanged = false;
            var mediaClosed = false;
            if (mediaTracked)
            {
                var read = await mediaSession.ReadCurrentAsync(cancellationToken);
                if (read.Status != MediaSessionReadStatus.Unavailable)
                {
                    media = read.Snapshot;
                    EvaluateMedia(media, DateTimeOffset.UtcNow, out mediaChanged, out mediaClosed);
                }
            }
            else
            {
                // 关闭媒体上报或进入隐私模式后不再跟踪，重新开启时会立即补报一次当前状态。
                ResetMediaTracking();
            }

            var now = DateTimeOffset.UtcNow;
            var changed = !string.Equals(title, _lastTitle, StringComparison.Ordinal);
            var heartbeatDue = now - _lastSentAt >= GetHeartbeat();
            if (forceTrigger is null && !changed && !mediaChanged && !mediaClosed && !heartbeatDue)
            {
                return;
            }

            var batchTrigger = forceTrigger
                ?? (changed
                    ? IngestProtocol.Triggers.Change
                    : mediaChanged || mediaClosed
                        ? IngestProtocol.Triggers.Media
                        : IngestProtocol.Triggers.Heartbeat);

            var windowEvent = new IngestEvent
            {
                Id = Guid.NewGuid().ToString("N"),
                Sequence = ++_sequence,
                Type = IngestProtocol.EventTypes.WindowActivity,
                OccurredAt = ProtocolTime.ToUtc(now),
                Trigger = changed ? IngestProtocol.Triggers.Change : batchTrigger,
                Data = new WindowActivityData
                {
                    Title = title,
                    App = snapshot.Application,
                    ProcessId = snapshot.ProcessId
                }
            };

            IngestEvent? mediaEvent = null;
            if (mediaChanged && media is not null)
            {
                mediaEvent = new IngestEvent
                {
                    Id = Guid.NewGuid().ToString("N"),
                    Sequence = ++_sequence,
                    Type = IngestProtocol.EventTypes.MediaPlayback,
                    OccurredAt = ProtocolTime.ToUtc(now),
                    Trigger = IngestProtocol.Triggers.Media,
                    Data = new MediaPlaybackData
                    {
                        Title = media.Title,
                        Artist = media.Artist,
                        Album = media.Album,
                        Status = media.PlaybackStatus,
                        Source = media.SourceApp
                    }
                };
            }
            else if (mediaClosed)
            {
                var closed = media ?? _lastMediaSnapshot;
                mediaEvent = new IngestEvent
                {
                    Id = Guid.NewGuid().ToString("N"),
                    Sequence = ++_sequence,
                    Type = IngestProtocol.EventTypes.MediaPlayback,
                    OccurredAt = ProtocolTime.ToUtc(now),
                    Trigger = IngestProtocol.Triggers.Media,
                    Data = new MediaPlaybackData
                    {
                        Title = closed?.Title ?? string.Empty,
                        Artist = closed?.Artist ?? string.Empty,
                        Album = closed?.Album ?? string.Empty,
                        Status = "closed",
                        Source = closed?.SourceApp ?? string.Empty
                    }
                };
            }

            // 服务端已明确表示不支持的类型不再重复发送，避免每轮都产生 ignored 噪声。
            var events = new List<IngestEvent>(2);
            if (!_unsupportedEventTypes.Contains(windowEvent.Type))
            {
                events.Add(windowEvent);
            }

            if (mediaEvent is not null && !_unsupportedEventTypes.Contains(mediaEvent.Type))
            {
                events.Add(mediaEvent);
            }

            IngestSendResult result;
            try
            {
                result = await SendWithRetryAsync(settings, session, events, cancellationToken);
            }
            catch (IngestRequestException exception)
            {
                await HandleIngestErrorAsync(exception, cancellationToken);
                return;
            }

            await HandleIngestResultAsync(result, events, cancellationToken);

            // 只有服务端真正持有的事件（accepted，或幂等命中 DUPLICATE）才算已上报，否则下一轮会重发。
            if (result.IsDelivered(windowEvent.Id))
            {
                _lastTitle = title;
            }

            if (mediaEvent is not null && mediaTracked && result.IsDelivered(mediaEvent.Id))
            {
                if (mediaClosed)
                {
                    _mediaCloseSent = true;
                }
                else
                {
                    _lastMediaKey = media?.Key;
                    _mediaCloseSent = false;
                }
            }

            _lastSentAt = now;
            var mediaText = mediaClosed ? null : media?.DisplayText;
            var mediaLog = mediaClosed ? "closed" : mediaText;
            await logger.LogAsync(
                $"[sent {batchTrigger}] {now.LocalDateTime:yyyy-MM-dd HH:mm:ss} | {snapshot.Application} - {title}" +
                (mediaLog is null ? string.Empty : $" | media: {mediaLog}"),
                cancellationToken);
            UsageSent?.Invoke(this, new UsageSentEventArgs(now.ToLocalTime(), snapshot.Application, title, mediaText));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            await logger.LogAsync($"[error] {exception.Message}", cancellationToken);
            Error?.Invoke(this, new MonitoringErrorEventArgs(exception.Message, stopsMonitoring: false));
        }
        finally
        {
            _tickLock.Release();
        }
    }

    /// <summary>
    /// 媒体生命周期判定（协议 §4.2.1）：会话从“有”变“无”时补一条 closed；
    /// 会话停留在非 playing 状态超过 <see cref="MediaIdleTimeout"/> 后也发一条 closed。
    /// 计时基准是进入非 playing 的时刻，心跳或重复上报不会刷新它。
    /// </summary>
    private void EvaluateMedia(
        MediaPlaybackSnapshot? media,
        DateTimeOffset now,
        out bool mediaChanged,
        out bool mediaClosed)
    {
        mediaChanged = false;
        mediaClosed = false;

        if (media is null)
        {
            _mediaNonPlayingSince = null;
            if (!_mediaCloseSent && _lastMediaKey is not null)
            {
                mediaClosed = true;
            }

            return;
        }

        _lastMediaSnapshot = media;
        var identity = MediaIdentity(media);
        var sameSession = string.Equals(identity, _mediaSessionIdentity, StringComparison.Ordinal);
        var closedSameSession = _mediaCloseSent && sameSession;
        _mediaSessionIdentity = identity;
        var sameKey = string.Equals(media.Key, _lastMediaKey, StringComparison.Ordinal);

        if (string.Equals(media.PlaybackStatus, "playing", StringComparison.Ordinal))
        {
            _mediaNonPlayingSince = null;
            mediaChanged = !sameKey || _mediaCloseSent;
            return;
        }

        if (string.Equals(media.PlaybackStatus, "closed", StringComparison.Ordinal))
        {
            _mediaNonPlayingSince = null;
            mediaClosed = !closedSameSession && _lastMediaKey is not null;
            return;
        }

        if (!sameSession)
        {
            _mediaNonPlayingSince = now;
        }

        _mediaNonPlayingSince ??= now;

        if (!closedSameSession && now - _mediaNonPlayingSince.Value >= MediaIdleTimeout)
        {
            mediaClosed = true;
            return;
        }

        if (!closedSameSession && !sameKey)
        {
            mediaChanged = true;
        }
    }

    private static string MediaIdentity(MediaPlaybackSnapshot media) =>
        $"{media.SourceApp}\u001f{media.Title}\u001f{media.Artist}\u001f{media.Album}";

    private void ResetMediaTracking()
    {
        _lastMediaSnapshot = null;
        _lastMediaKey = null;
        _mediaSessionIdentity = null;
        _mediaNonPlayingSince = null;
        _mediaCloseSent = false;
    }

    /// <summary>
    /// 客户端侧标题长度策略。服务端通过 FIELD_TOO_LONG 的 details.limit 可以进一步收紧。
    /// </summary>
    private string ApplyTitlePolicy(string title, MonitorSettings settings)
    {
        var limit = settings.ForceAllowLongTitle ? 0 : DefaultTitleLimit;
        if (_serverTitleLimit is > 0 && (limit == 0 || _serverTitleLimit.Value < limit))
        {
            limit = _serverTitleLimit.Value;
        }

        if (limit <= 0 || title.Length <= limit)
        {
            return title;
        }

        return title[..Math.Max(1, limit - TitleTruncateMargin)];
    }

    private async Task HandleIngestResultAsync(
        IngestSendResult result,
        IReadOnlyList<IngestEvent> events,
        CancellationToken cancellationToken)
    {
        if (result.ProtocolVersion != 0 && result.ProtocolVersion != IngestProtocol.Version)
        {
            await logger.LogAsync(
                $"[protocol] server replied with version {result.ProtocolVersion}, client uses {IngestProtocol.Version}",
                cancellationToken);
        }

        if (result.Pacing?.NextUploadAfterMs is int nextUploadAfterMs)
        {
            // 服务端主动限速：作为执行间隔的下限，钳制在 0-60s 以免服务端误配时把客户端卡死。
            lock (_stateLock)
            {
                _serverPacing = TimeSpan.FromMilliseconds(Math.Clamp(nextUploadAfterMs, 0, 60_000));
            }
        }

        foreach (var ignored in result.Ignored)
        {
            if (string.Equals(ignored.Code, IngestProtocol.ErrorCodes.UnsupportedType, StringComparison.Ordinal)
                && _unsupportedEventTypes.Add(ignored.Type))
            {
                await logger.LogAsync(
                    $"[unsupported] server does not accept '{ignored.Type}'; it will no longer be sent",
                    cancellationToken);
            }
            else
            {
                await logger.LogAsync($"[ignored] type='{ignored.Type}' code='{ignored.Code}'", cancellationToken);
            }
        }

        foreach (var rejection in result.Rejected)
        {
            var type = events.FirstOrDefault(item => item.Id == rejection.Id)?.Type ?? "unknown";
            var message = string.IsNullOrWhiteSpace(rejection.Message) ? string.Empty : $" - {rejection.Message}";
            await logger.LogAsync(
                $"[rejected] type='{type}' code='{rejection.Code}' retryable={rejection.Retryable}{message}",
                cancellationToken);

            if (string.Equals(rejection.Code, IngestProtocol.ErrorCodes.FieldTooLong, StringComparison.Ordinal))
            {
                // 协议 §6.2：仅当 details.field 为 title（或服务端未给 field）时才把该上限应用到标题。
                // 不能把别的字段（如 album）的 limit 误用到标题上。
                var limit = rejection.GetDetailInt("limit", "title");
                if (limit is > 0 and <= 4096)
                {
                    _serverTitleLimit = limit;
                }
            }
        }
    }

    private async Task<IngestSendResult> SendWithRetryAsync(
        MonitorSettings settings,
        IngestSessionInfo session,
        IReadOnlyList<IngestEvent> events,
        CancellationToken cancellationToken)
    {
        var retryCount = 0;
        while (true)
        {
            try
            {
                return await ingest.SendAsync(settings, session, events, cancellationToken);
            }
            catch (IngestRequestException exception)
                when (exception.Retryable &&
                      !IsRateLimited(exception) &&
                      retryCount < MaxServerErrorRetries)
            {
                retryCount++;
                await logger.LogAsync(
                    $"[retry] {exception.StatusCode} {exception.Code}, attempt {retryCount}/{MaxServerErrorRetries}",
                    cancellationToken);
                await Task.Delay(
                    TimeSpan.FromMilliseconds(500 * (1 << (retryCount - 1))),
                    cancellationToken);
            }
        }
    }

    /// <summary>
    /// 限流走专门的 retry_after_ms 退避路径（协议 §7.1），不应消耗指数退避的重试预算。
    /// </summary>
    private static bool IsRateLimited(IngestRequestException exception) =>
        string.Equals(exception.Code, IngestProtocol.ErrorCodes.RateLimited, StringComparison.Ordinal);

    private async Task HandleIngestErrorAsync(IngestRequestException exception, CancellationToken cancellationToken)
    {
        await logger.LogAsync($"[error] {exception.Message}", cancellationToken);

        if (IsRateLimited(exception))
        {
            var milliseconds = Math.Clamp((exception.RetryAfterMs ?? 800) + 250, 300, 5000);
            await logger.LogAsync($"[rate-limit] backoff {milliseconds}ms", cancellationToken);
            await Task.Delay(milliseconds, cancellationToken);
            return;
        }

        SetRunning(false);
        _monitorCancellation?.Cancel();
        Error?.Invoke(this, new MonitoringErrorEventArgs(
            exception.RawBody.Length == 0 ? exception.Message : exception.RawBody,
            stopsMonitoring: true,
            statusCode: exception.StatusCode,
            code: exception.Code));
    }

    private void SetRunning(bool running)
    {
        bool changed;
        lock (_stateLock)
        {
            changed = _isRunning != running;
            _isRunning = running;
        }

        if (changed)
        {
            StatusChanged?.Invoke(this, new MonitoringStatusChangedEventArgs(running));
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await StopAsync();
        _tickLock.Dispose();
    }
}
