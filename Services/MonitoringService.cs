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
    void UpdateRuntimeSettings(int intervalSeconds, int heartbeatSeconds, bool forceAllowLongTitle);
    Task SendCurrentAsync(bool privacyMode, CancellationToken cancellationToken);
}

public sealed class MonitoringService(
    IForegroundWindowService foregroundWindow,
    IUsageIngestService ingest,
    IAppLogger logger) : IMonitoringService
{
    private readonly object _stateLock = new();
    private readonly SemaphoreSlim _tickLock = new(1, 1);
    private CancellationTokenSource? _monitorCancellation;
    private Task? _loopTask;
    private MonitorSettings? _settings;
    private string? _lastTitle;
    private DateTimeOffset _lastSentAt = DateTimeOffset.MinValue;
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
            _lastSentAt = DateTimeOffset.MinValue;
            _isRunning = true;
        }

        StatusChanged?.Invoke(this, new MonitoringStatusChangedEventArgs(true));
        await TickAsync(token, force: true);
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
            await TickAsync(linkedCancellation.Token, force: true);
        }
    }

    public void UpdateRuntimeSettings(int intervalSeconds, int heartbeatSeconds, bool forceAllowLongTitle)
    {
        lock (_stateLock)
        {
            if (_settings is null)
            {
                return;
            }

            _settings = _settings with
            {
                IntervalSeconds = Math.Clamp(intervalSeconds, 5, 3600),
                HeartbeatSeconds = Math.Clamp(heartbeatSeconds, 10, 3600),
                ForceAllowLongTitle = forceAllowLongTitle
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
                await TickAsync(cancellationToken, force: false);
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
            return TimeSpan.FromSeconds(Math.Clamp(_settings?.IntervalSeconds ?? 5, 5, 3600));
        }
    }

    private TimeSpan GetHeartbeat()
    {
        lock (_stateLock)
        {
            return TimeSpan.FromSeconds(Math.Clamp(_settings?.HeartbeatSeconds ?? 10, 10, 3600));
        }
    }

    private async Task TickAsync(CancellationToken cancellationToken, bool force)
    {
        await _tickLock.WaitAsync(cancellationToken);
        try
        {
            MonitorSettings? settings;
            lock (_stateLock)
            {
                settings = _settings;
            }

            if (settings is null || !IsRunning)
            {
                return;
            }

            var snapshot = settings.PrivacyMode
                ? new ForegroundWindowSnapshot("TA现在不想给你看QAQ", "private mode", 0)
                : foregroundWindow.ReadCurrent();
            var title = snapshot.Title;
            if (!settings.ForceAllowLongTitle && title.Length > 150)
            {
                title = title[..140];
            }

            var now = DateTimeOffset.UtcNow;
            var changed = !string.Equals(title, _lastTitle, StringComparison.Ordinal);
            var heartbeatDue = now - _lastSentAt >= GetHeartbeat();
            if (!force && !changed && !heartbeatDue)
            {
                return;
            }

            var reason = changed ? "change" : "heartbeat";
            var payload = new UploadEvent
            {
                Machine = settings.MachineId,
                WindowTitle = title,
                Application = snapshot.Application,
                Raw = new RawUploadInfo
                {
                    Exe = snapshot.Application,
                    ProcessId = snapshot.ProcessId,
                    Reason = reason
                }
            };

            try
            {
                await ingest.SendAsync(payload, settings, cancellationToken);
            }
            catch (IngestErrorException exception)
            {
                await HandleIngestErrorAsync(exception, snapshot, title, cancellationToken);
                return;
            }

            _lastTitle = title;
            _lastSentAt = now;
            await logger.LogAsync($"[sent {reason}] {now.LocalDateTime:yyyy-MM-dd HH:mm:ss} | {snapshot.Application} - {title}", cancellationToken);
            UsageSent?.Invoke(this, new UsageSentEventArgs(now.ToLocalTime(), snapshot.Application, title));
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

    private async Task HandleIngestErrorAsync(
        IngestErrorException exception,
        ForegroundWindowSnapshot snapshot,
        string title,
        CancellationToken cancellationToken)
    {
        await logger.LogAsync($"[error] {exception.Message}", cancellationToken);
        if (exception.StatusCode == 429)
        {
            var milliseconds = Math.Clamp(ServerErrorParser.ParseRetryAfterMilliseconds(exception.RawBody) + 250, 300, 5000);
            await logger.LogAsync($"[rate-limit] backoff {milliseconds}ms", cancellationToken);
            await Task.Delay(milliseconds, cancellationToken);
            return;
        }

        if (ServerErrorParser.IsWindowTitleTooLong(exception))
        {
            var (limit, length) = ServerErrorParser.ExtractLimitLength(exception.ServerError ?? exception.RawBody);
            var limitText = limit?.ToString() ?? "?";
            var lengthText = length?.ToString() ?? "?";
            await logger.LogAsync($"[title-too-long] submitted app='{snapshot.Application}' | title='{title}' (limit={limitText}, length={lengthText})", cancellationToken);
        }

        SetRunning(false);
        _monitorCancellation?.Cancel();
        Error?.Invoke(this, new MonitoringErrorEventArgs(
            exception.RawBody.Length == 0 ? exception.Message : exception.RawBody,
            stopsMonitoring: true));
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
