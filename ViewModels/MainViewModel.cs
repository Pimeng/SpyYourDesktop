using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using Desktop.Infrastructure;
using Desktop.Models;
using Desktop.Services;
using Microsoft.UI.Dispatching;

namespace Desktop.ViewModels;

public sealed class MainViewModel : INotifyPropertyChanged, IAsyncDisposable
{
    private const int MinimumIntervalSeconds = 5;
    private const int MinimumHeartbeatSeconds = 10;

    private readonly IConfigurationStore _configurationStore;
    private readonly IStartupService _startupService;
    private readonly IMonitoringService _monitoringService;
    private readonly IUpdateService _updateService;
    private readonly IAppLogger _logger;
    private readonly AppPaths _paths;
    private readonly IApplicationLifetime _applicationLifetime;
    private readonly DispatcherQueue _dispatcherQueue;
    private readonly CancellationTokenSource _applicationCancellation = new();
    private Task? _privacyTask;
    private Task? _startupTask;
    private Task? _updateCheckTask;
    private CancellationTokenSource? _configurationSaveCancellation;
    private Task? _configurationSaveTask;

    private string _serverUrl = "http://127.0.0.1:3000/api/ingest";
    private int _intervalSeconds = MinimumIntervalSeconds;
    private int _heartbeatSeconds = MinimumHeartbeatSeconds;
    private string _machineId = string.Empty;
    private string _uploadKey = string.Empty;
    private bool _showKey = true;
    private bool _autoStart;
    private bool _allowBackground;
    private bool _privacyMode;
    private bool _forceAllowLongTitle;
    private bool _isRunning;
    private bool _isBusy;
    private bool _isInitialized;
    private bool _isDisposed;
    private string _statusText = "未运行";
    private string _lastSentAt = "尚未上报";
    private string _lastApplication = "尚未采集";
    private string _lastTitle = "尚未采集";
    private string _errorMessage = string.Empty;
    private string _noticeMessage = string.Empty;
    private bool _isErrorVisible;
    private bool _isNoticeVisible;
    private UpdateRelease? _availableUpdate;
    private string _updateStatus = string.Empty;
    private int _updateProgressPercent;
    private string? _skippedVersion;
    private bool _canPersistConfiguration = true;

    public MainViewModel(
        IConfigurationStore configurationStore,
        IStartupService startupService,
        IMonitoringService monitoringService,
        IUpdateService updateService,
        IAppLogger logger,
        AppPaths paths,
        IApplicationLifetime applicationLifetime,
        DispatcherQueue dispatcherQueue)
    {
        _configurationStore = configurationStore;
        _startupService = startupService;
        _monitoringService = monitoringService;
        _updateService = updateService;
        _logger = logger;
        _paths = paths;
        _applicationLifetime = applicationLifetime;
        _dispatcherQueue = dispatcherQueue;

        StartCommand = new AsyncCommand(StartMonitoringAsync, () => !IsRunning && !IsBusy);
        StopCommand = new AsyncCommand(StopMonitoringAsync, () => IsRunning && !IsBusy);
        CheckForUpdatesCommand = new AsyncCommand(() => StartUpdateCheckAsync(manual: true), () => !IsBusy);
        ApplyUpdateCommand = new AsyncCommand(ApplyUpdateAsync, () => AvailableUpdate is not null && !IsBusy);
        SkipUpdateCommand = new AsyncCommand(SkipUpdateAsync, () => AvailableUpdate is not null && !IsBusy);
        DismissNoticeCommand = new RelayCommand(DismissNotice);
        OpenReleaseCommand = new RelayCommand(OpenReleasePage, () => AvailableUpdate is not null);
        OpenLogCommand = new RelayCommand(OpenLogFile);

        _monitoringService.StatusChanged += OnMonitoringStatusChanged;
        _monitoringService.UsageSent += OnUsageSent;
        _monitoringService.Error += OnMonitoringError;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public CancellationToken ApplicationCancellationToken => _applicationCancellation.Token;

    public void CancelPendingOperations() => _applicationCancellation.Cancel();

    public ICommand StartCommand { get; }
    public ICommand StopCommand { get; }
    public ICommand CheckForUpdatesCommand { get; }
    public ICommand ApplyUpdateCommand { get; }
    public ICommand SkipUpdateCommand { get; }
    public ICommand DismissNoticeCommand { get; }
    public ICommand OpenReleaseCommand { get; }
    public ICommand OpenLogCommand { get; }

    public string ServerUrl
    {
        get => _serverUrl;
        set => SetProperty(ref _serverUrl, value);
    }

    public int IntervalSeconds
    {
        get => _intervalSeconds;
        set => SetProperty(ref _intervalSeconds, Math.Clamp(value, MinimumIntervalSeconds, 3600));
    }

    public int HeartbeatSeconds
    {
        get => _heartbeatSeconds;
        set => SetProperty(ref _heartbeatSeconds, Math.Clamp(value, MinimumHeartbeatSeconds, 3600));
    }

    public string MachineId
    {
        get => _machineId;
        set => SetProperty(ref _machineId, value);
    }

    public string UploadKey
    {
        get => _uploadKey;
        set => SetProperty(ref _uploadKey, value);
    }

    public bool ShowKey
    {
        get => _showKey;
        set => SetProperty(ref _showKey, value);
    }

    public bool AutoStart
    {
        get => _autoStart;
        set
        {
            if (!SetProperty(ref _autoStart, value) || !_isInitialized)
            {
                return;
            }

            _startupTask = UpdateStartupAsync(value);
        }
    }

    public bool AllowBackground
    {
        get => _allowBackground;
        set => SetProperty(ref _allowBackground, value);
    }

    public bool PrivacyMode
    {
        get => _privacyMode;
        set
        {
            if (!SetProperty(ref _privacyMode, value) || !_isInitialized || !IsRunning)
            {
                return;
            }

            _privacyTask = SendAfterPrivacyChangeAsync(value);
        }
    }

    public bool ForceAllowLongTitle
    {
        get => _forceAllowLongTitle;
        set => SetProperty(ref _forceAllowLongTitle, value);
    }

    public bool IsRunning
    {
        get => _isRunning;
        private set
        {
            if (!SetProperty(ref _isRunning, value))
            {
                return;
            }

            StatusText = value ? "运行中" : "未运行";
            RaiseCommandStates();
            OnPropertyChanged(nameof(CanEditSettings));
        }
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (!SetProperty(ref _isBusy, value))
            {
                return;
            }

            RaiseCommandStates();
            OnPropertyChanged(nameof(CanEditSettings));
        }
    }

    public bool CanEditSettings => !IsRunning && !IsBusy;
    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public string GreetingText => $"{GetTimeGreeting()}，{GetCurrentUserName()}";

    private static string GetTimeGreeting() => DateTime.Now.Hour switch
    {
        >= 5 and < 12 => "早上好",
        >= 12 and < 14 => "中午好",
        >= 14 and < 19 => "下午好",
        >= 19 => "晚上好",
        _ => "夜深了"
    };

    private static string GetCurrentUserName()
    {
        try
        {
            var userName = Environment.UserName.Trim();
            return string.IsNullOrWhiteSpace(userName) ? "朋友" : userName;
        }
        catch
        {
            return "朋友";
        }
    }

    public string LastSentAt
    {
        get => _lastSentAt;
        private set => SetProperty(ref _lastSentAt, value);
    }

    public string LastApplication
    {
        get => _lastApplication;
        private set => SetProperty(ref _lastApplication, value);
    }

    public string LastTitle
    {
        get => _lastTitle;
        private set => SetProperty(ref _lastTitle, value);
    }

    public bool IsErrorVisible
    {
        get => _isErrorVisible;
        private set => SetProperty(ref _isErrorVisible, value);
    }

    public string ErrorMessage
    {
        get => _errorMessage;
        private set => SetProperty(ref _errorMessage, value);
    }

    public bool IsNoticeVisible
    {
        get => _isNoticeVisible;
        private set
        {
            if (SetProperty(ref _isNoticeVisible, value))
            {
                OnPropertyChanged(nameof(AreUpdateActionsVisible));
            }
        }
    }

    public string NoticeMessage
    {
        get => _noticeMessage;
        private set => SetProperty(ref _noticeMessage, value);
    }

    public UpdateRelease? AvailableUpdate
    {
        get => _availableUpdate;
        private set
        {
            if (!SetProperty(ref _availableUpdate, value))
            {
                return;
            }

            OnPropertyChanged(nameof(HasAvailableUpdate));
            OnPropertyChanged(nameof(AreUpdateActionsVisible));
            OnPropertyChanged(nameof(UpdateTag));
            OnPropertyChanged(nameof(UpdateNotes));
            OnPropertyChanged(nameof(CanApplyInPlace));
            OnPropertyChanged(nameof(UpdateActionText));
            RaiseCommandStates();
        }
    }

    public bool HasAvailableUpdate => AvailableUpdate is not null;
    public bool AreUpdateActionsVisible => IsNoticeVisible && HasAvailableUpdate;
    public string UpdateTag => AvailableUpdate?.Tag ?? string.Empty;
    public string UpdateNotes => AvailableUpdate?.Notes ?? string.Empty;
    public bool CanApplyInPlace => _updateService.CanApplyInPlace;
    public string UpdateActionText => CanApplyInPlace ? "下载更新" : "打开发布页";

    public string UpdateStatus
    {
        get => _updateStatus;
        private set => SetProperty(ref _updateStatus, value);
    }

    public int UpdateProgressPercent
    {
        get => _updateProgressPercent;
        private set => SetProperty(ref _updateProgressPercent, value);
    }

    public async Task InitializeAsync(string commandLine, CancellationToken cancellationToken = default)
    {
        if (_isInitialized)
        {
            return;
        }

        var config = await _configurationStore.LoadAsync(cancellationToken);
        _canPersistConfiguration = _configurationStore.LastLoadSucceeded;
        _serverUrl = string.IsNullOrWhiteSpace(config.ServerUrl)
            ? "http://127.0.0.1:3000/api/ingest"
            : config.ServerUrl;
        _intervalSeconds = Math.Clamp(config.IntervalSeconds <= 0 ? MinimumIntervalSeconds : config.IntervalSeconds, MinimumIntervalSeconds, 3600);
        _heartbeatSeconds = Math.Clamp(config.HeartbeatSeconds <= 0 ? MinimumHeartbeatSeconds : config.HeartbeatSeconds, MinimumHeartbeatSeconds, 3600);
        _machineId = config.MachineId?.Trim() ?? string.Empty;
        _uploadKey = config.UploadKey ?? string.Empty;
        _allowBackground = config.AllowBackground;
        _forceAllowLongTitle = config.ForceAllowLongTitle;
        _skippedVersion = config.SkippedVersion;
        _privacyMode = false;

        RaiseAllSettingsChanged();
        _isInitialized = true;

        var startupEnabled = await Task.Run(_startupService.IsEnabled, cancellationToken);
        if (startupEnabled)
        {
            _autoStart = true;
            OnPropertyChanged(nameof(AutoStart));
        }
        else
        {
            _autoStart = config.AutoStart;
            OnPropertyChanged(nameof(AutoStart));
        }

        await _logger.LogAsync("=== AppUsageMonitor started ===", cancellationToken);

        var arguments = commandLine.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var elevatedIndex = Array.FindIndex(arguments, argument =>
            string.Equals(argument, "--elevated-update", StringComparison.OrdinalIgnoreCase));
        if (elevatedIndex >= 0 && elevatedIndex + 1 < arguments.Length)
        {
            var updateUrl = arguments[elevatedIndex + 1].Trim('"');
            var tag = elevatedIndex + 2 < arguments.Length ? arguments[elevatedIndex + 2].Trim('"') : "latest";
            var release = new UpdateRelease(tag, updateUrl, updateUrl, string.Empty, ParseVersion(tag));
            await ApplyUpdateAsync(release, cancellationToken);
            return;
        }

        if (CanAutoStart())
        {
            await StartMonitoringAsync(showValidation: false);
        }

        _updateCheckTask = StartUpdateCheckAsync(manual: false);
    }

    private Task StartUpdateCheckAsync(bool manual)
    {
        var task = CheckForUpdatesAsync(manual);
        _updateCheckTask = task;
        return task;
    }

    private async Task StartMonitoringAsync() => await StartMonitoringAsync(showValidation: true);

    private async Task StartMonitoringAsync(bool showValidation)
    {
        ClearMessages();
        if (!TryCreateSettings(out var settings, out var validationMessage))
        {
            if (showValidation)
            {
                ShowError(validationMessage);
            }

            return;
        }

        IsBusy = true;
        try
        {
            await SaveConfigurationAsync();
            await _monitoringService.StartAsync(settings, _applicationCancellation.Token);
            if (_monitoringService.IsRunning)
            {
                IsRunning = true;
                ShowNotice("监控已启动，正在等待前台应用发生变化或心跳到期。");
            }
        }
        catch (Exception exception)
        {
            await _logger.LogAsync($"[start] {exception.Message}");
            ShowError($"无法开始监控：{exception.Message}");
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task StopMonitoringAsync()
    {
        ClearMessages();
        IsBusy = true;
        try
        {
            await _monitoringService.StopAsync();
            IsRunning = false;
            ShowNotice("监控已停止。");
        }
        catch (Exception exception)
        {
            await _logger.LogAsync($"[stop] {exception.Message}");
            ShowError($"无法停止监控：{exception.Message}");
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task CheckForUpdatesAsync(bool manual)
    {
        if (manual)
        {
            IsBusy = true;
            ClearMessages();
            UpdateStatus = "正在检查更新...";
        }

        try
        {
            var release = await _updateService.CheckLatestAsync(_applicationCancellation.Token);
            if (release is null)
            {
                if (manual)
                {
                    ShowNotice("没有获取到最新版本信息。");
                }

                return;
            }

            if (!manual && string.Equals(_skippedVersion, release.Tag, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            if (release.Version > ParseVersion(_updateService.CurrentVersion))
            {
                AvailableUpdate = release;
                UpdateStatus = $"发现新版本 {release.Tag}";
                ShowNotice($"发现 SpyYourDesktop 新版本 {release.Tag}。");
            }
            else if (manual)
            {
                ShowNotice($"当前已是最新版本（{_updateService.CurrentVersion}）。");
            }
        }
        catch (OperationCanceledException) when (_applicationCancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            await _logger.LogAsync($"[update-check] {exception.Message}");
            if (manual)
            {
                ShowError($"检查更新失败：{exception.Message}");
            }
        }
        finally
        {
            if (manual)
            {
                IsBusy = false;
            }
        }
    }

    private async Task ApplyUpdateAsync() =>
        await ApplyUpdateAsync(AvailableUpdate, _applicationCancellation.Token);

    private async Task ApplyUpdateAsync(UpdateRelease? release, CancellationToken cancellationToken)
    {
        if (release is null)
        {
            return;
        }

        IsBusy = true;
        try
        {
            UpdateProgressPercent = 0;
            UpdateStatus = "正在下载更新...";
            var progress = new Progress<UpdateProgress>(value =>
            {
                UpdateProgressPercent = value.Percent;
                UpdateStatus = value.TotalBytes is > 0
                    ? $"正在下载更新... {value.Percent}%"
                    : $"正在下载更新... {value.DownloadedBytes / 1024} KB";
            });
            await _updateService.DownloadAndApplyAsync(release, progress, cancellationToken);

            if (_updateService.CanApplyInPlace)
            {
                UpdateStatus = "更新已准备完成，应用即将重启。";
                _applicationLifetime.RequestExit();
            }
            else
            {
                UpdateStatus = "当前安装由系统管理，已打开 GitHub 发布页。";
                ShowNotice(UpdateStatus);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            await _logger.LogAsync($"[update-apply] {exception.Message}");
            ShowError($"下载更新失败：{exception.Message}");
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task SkipUpdateAsync()
    {
        if (AvailableUpdate is null)
        {
            return;
        }

        IsBusy = true;
        try
        {
            _skippedVersion = AvailableUpdate.Tag;
            await SaveConfigurationAsync();
            AvailableUpdate = null;
            UpdateStatus = "已跳过此版本。";
            DismissNotice();
        }
        catch (Exception exception)
        {
            await _logger.LogAsync($"[update-skip] {exception.Message}");
            ShowError($"无法保存更新设置：{exception.Message}");
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void DismissNotice() => IsNoticeVisible = false;

    private void OpenReleasePage()
    {
        if (AvailableUpdate is not null)
        {
            try
            {
                _updateService.OpenReleasePage(AvailableUpdate);
            }
            catch (Exception exception)
            {
                ShowError($"无法打开发布页：{exception.Message}");
            }
        }
    }

    private void OpenLogFile()
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = _paths.LogFile,
                UseShellExecute = true
            });
        }
        catch (Exception exception)
        {
            ShowError($"无法打开日志：{exception.Message}");
        }
    }

    private async Task UpdateStartupAsync(bool enabled)
    {
        try
        {
            await Task.Run(() => _startupService.SetEnabled(enabled), _applicationCancellation.Token);
        }
        catch (OperationCanceledException) when (_applicationCancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            await _logger.LogAsync($"[startup] {exception.Message}");
            RunOnUi(() =>
            {
                _autoStart = !enabled;
                OnPropertyChanged(nameof(AutoStart));
                ShowError("设置开机自启动失败，可能没有权限。");
            });
        }
    }

    private async Task SendAfterPrivacyChangeAsync(bool privacyMode)
    {
        try
        {
            await _monitoringService.SendCurrentAsync(privacyMode, _applicationCancellation.Token);
        }
        catch (OperationCanceledException) when (_applicationCancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            await _logger.LogAsync($"[privacy] {exception.Message}");
            ShowError($"切换隐私模式后上报失败：{exception.Message}");
        }
    }

    private async Task SaveConfigurationAsync()
        => await SaveConfigurationAsync(_applicationCancellation.Token, force: true);

    private void QueueConfigurationSave()
    {
        if (!_isInitialized || _isDisposed)
        {
            return;
        }

        _configurationSaveCancellation?.Cancel();
        var cancellation = new CancellationTokenSource();
        _configurationSaveCancellation = cancellation;
        _configurationSaveTask = SaveConfigurationAfterDelayAsync(cancellation);
    }

    private async Task SaveConfigurationAfterDelayAsync(CancellationTokenSource cancellation)
    {
        try
        {
            await Task.Delay(TimeSpan.FromMilliseconds(300), cancellation.Token);
            await SaveConfigurationAsync(cancellation.Token, force: true);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            await _logger.LogAsync($"[config-save] {exception.Message}");
            ShowError($"配置保存失败：{exception.Message}");
        }
        finally
        {
            if (ReferenceEquals(_configurationSaveCancellation, cancellation))
            {
                _configurationSaveCancellation = null;
            }

            cancellation.Dispose();
        }
    }

    private async Task SaveConfigurationAsync(CancellationToken cancellationToken, bool force = false)
    {
        if (!_canPersistConfiguration && !force)
        {
            return;
        }

        await _configurationStore.SaveAsync(new AppConfig
        {
            ServerUrl = ServerUrl.Trim(),
            IntervalSeconds = Math.Clamp(IntervalSeconds, MinimumIntervalSeconds, 3600),
            HeartbeatSeconds = Math.Clamp(HeartbeatSeconds, MinimumHeartbeatSeconds, 3600),
            MachineId = MachineId.Trim(),
            UploadKey = UploadKey,
            AutoStart = AutoStart,
            AllowBackground = AllowBackground,
            SkippedVersion = _skippedVersion,
            ForceAllowLongTitle = ForceAllowLongTitle
        }, cancellationToken);
    }

    private bool CanAutoStart() =>
        ServerUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase) &&
        !string.IsNullOrWhiteSpace(MachineId) &&
        !string.IsNullOrWhiteSpace(UploadKey) &&
        IntervalSeconds >= MinimumIntervalSeconds &&
        HeartbeatSeconds >= MinimumHeartbeatSeconds;

    private bool TryCreateSettings(out MonitorSettings settings, out string message)
    {
        if (!Uri.TryCreate(ServerUrl.Trim(), UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            settings = null!;
            message = "服务器地址必须是 http 或 https 地址。";
            return false;
        }

        if (string.IsNullOrWhiteSpace(MachineId))
        {
            settings = null!;
            message = "请填写设备 ID。";
            return false;
        }

        settings = new MonitorSettings(
            uri.ToString(),
            Math.Clamp(IntervalSeconds, MinimumIntervalSeconds, 3600),
            Math.Clamp(HeartbeatSeconds, MinimumHeartbeatSeconds, 3600),
            MachineId.Trim(),
            UploadKey,
            PrivacyMode,
            ForceAllowLongTitle);
        message = string.Empty;
        return true;
    }

    private void OnMonitoringStatusChanged(object? sender, MonitoringStatusChangedEventArgs args) =>
        RunOnUi(() => IsRunning = args.IsRunning);

    private void OnUsageSent(object? sender, UsageSentEventArgs args) =>
        RunOnUi(() =>
        {
            LastSentAt = args.SentAt.LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss");
            LastApplication = args.Application;
            LastTitle = args.Title;
        });

    private void OnMonitoringError(object? sender, MonitoringErrorEventArgs args) =>
        RunOnUi(() =>
        {
            if (args.StopsMonitoring)
            {
                IsRunning = false;
                ShowError($"上报失败，监控已停止：{args.Message}");
            }
            else
            {
                ShowNotice($"本次上报失败，监控仍在运行：{args.Message}");
            }
        });

    private void RunOnUi(Action action)
    {
        if (_dispatcherQueue.HasThreadAccess)
        {
            action();
        }
        else
        {
            _dispatcherQueue.TryEnqueue(new DispatcherQueueHandler(action));
        }
    }

    private void ShowError(string message)
    {
        RunOnUi(() =>
        {
            ErrorMessage = message;
            IsErrorVisible = true;
            IsNoticeVisible = false;
        });
    }

    private void ShowNotice(string message)
    {
        RunOnUi(() =>
        {
            NoticeMessage = message;
            IsNoticeVisible = true;
            IsErrorVisible = false;
        });
    }

    private void ClearMessages()
    {
        IsErrorVisible = false;
        IsNoticeVisible = false;
    }

    private void RaiseAllSettingsChanged()
    {
        OnPropertyChanged(nameof(ServerUrl));
        OnPropertyChanged(nameof(IntervalSeconds));
        OnPropertyChanged(nameof(HeartbeatSeconds));
        OnPropertyChanged(nameof(MachineId));
        OnPropertyChanged(nameof(UploadKey));
        OnPropertyChanged(nameof(AllowBackground));
        OnPropertyChanged(nameof(PrivacyMode));
        OnPropertyChanged(nameof(ForceAllowLongTitle));
    }

    private void RaiseCommandStates()
    {
        (StartCommand as AsyncCommand)?.RaiseCanExecuteChanged();
        (StopCommand as AsyncCommand)?.RaiseCanExecuteChanged();
        (CheckForUpdatesCommand as AsyncCommand)?.RaiseCanExecuteChanged();
        (ApplyUpdateCommand as AsyncCommand)?.RaiseCanExecuteChanged();
        (SkipUpdateCommand as AsyncCommand)?.RaiseCanExecuteChanged();
        (OpenReleaseCommand as RelayCommand)?.RaiseCanExecuteChanged();
    }

    private static Version ParseVersion(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return new Version(0, 0, 0, 0);
        }

        var normalized = value.Trim().TrimStart('v', 'V');
        var separator = normalized.IndexOfAny(['-', '+']);
        if (separator >= 0)
        {
            normalized = normalized[..separator];
        }

        return Version.TryParse(normalized, out var version)
            ? version
            : new Version(0, 0, 0, 0);
    }

    private bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        if (_isInitialized && propertyName is
            nameof(ServerUrl) or
            nameof(IntervalSeconds) or
            nameof(HeartbeatSeconds) or
            nameof(MachineId) or
            nameof(UploadKey) or
            nameof(AutoStart) or
            nameof(AllowBackground) or
            nameof(ForceAllowLongTitle))
        {
            QueueConfigurationSave();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;
        _applicationCancellation.Cancel();
        _configurationSaveCancellation?.Cancel();
        _monitoringService.StatusChanged -= OnMonitoringStatusChanged;
        _monitoringService.UsageSent -= OnUsageSent;
        _monitoringService.Error -= OnMonitoringError;

        try
        {
            if (_privacyTask is not null)
            {
                await _privacyTask;
            }

            if (_startupTask is not null)
            {
                await _startupTask;
            }

            if (_updateCheckTask is not null)
            {
                await _updateCheckTask;
            }

            if (_configurationSaveTask is not null)
            {
                await _configurationSaveTask;
            }

            await _monitoringService.StopAsync();
            if (_isInitialized)
            {
                try
                {
                    await SaveConfigurationAsync(CancellationToken.None, force: true);
                }
                catch (Exception exception)
                {
                    await _logger.LogAsync($"[config-save] {exception.Message}");
                }
            }

            await _logger.LogAsync("=== AppUsageMonitor stopped ===");
        }
        finally
        {
            await _monitoringService.DisposeAsync();
            if (_logger is IAsyncDisposable asyncLogger)
            {
                await asyncLogger.DisposeAsync();
            }

            _applicationCancellation.Dispose();
        }
    }
}
