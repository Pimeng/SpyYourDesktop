using Desktop.Infrastructure;
using Desktop.Services;
using Desktop.ViewModels;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using System.Threading;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace Desktop
{
    /// <summary>
    /// Provides application-specific behavior to supplement the default Application class.
    /// </summary>
    public partial class App : Application
    {
        private const string InstanceMutexName = "SpyYourDesktop_Singleton";
        private const string ShowWindowEventName = "SpyYourDesktop_ShowWindow";
        private readonly Mutex _instanceMutex;
        private readonly EventWaitHandle _showWindowEvent;
        private readonly AppPaths _paths = null!;
        private readonly FileLogger _logger = null!;
        private readonly MainViewModelFactory? _factory;
        private MainWindow? _window;
        private MainViewModel? _viewModel;
        private TrayService? _tray;
        private WindowsNotificationService? _notifications;
        private RegisteredWaitHandle? _showWindowWait;
        private Task? _initializationTask;
        private bool _shutdownStarted;

        /// <summary>
        /// Initializes the singleton application object.  This is the first line of authored code
        /// executed, and as such is the logical equivalent of main() or WinMain().
        /// </summary>
        public App()
        {
            InitializeComponent();

            // Create the event before checking the mutex so a fast second launch cannot miss the signal.
            _showWindowEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowWindowEventName);
            _instanceMutex = new Mutex(true, InstanceMutexName, out var createdNew);
            if (!createdNew)
            {
                if (!(HasProcessArgument("--startup") && HasProcessArgument("--minimized")))
                {
                    _showWindowEvent.Set();
                }

                return;
            }

            _paths = new AppPaths();
            _logger = new FileLogger(_paths);
            _factory = new MainViewModelFactory(_paths, _logger);
        }

        /// <summary>
        /// Invoked when the application is launched.
        /// </summary>
        /// <param name="args">Details about the launch request and process.</param>
        protected override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
        {
            if (_factory is null)
            {
                // There is no window to keep this secondary process alive.
                Environment.Exit(0);
                return;
            }

            if (_window is not null)
            {
                _window.Activate();
                return;
            }

            var dispatcherQueue = DispatcherQueue.GetForCurrentThread();
            var lifetime = new ApplicationLifetime(dispatcherQueue, () => _ = ShutdownAndCloseAsync());
            _tray = new TrayService();
            _notifications = new WindowsNotificationService();
            _notifications.Register();
            if (!_notifications.IsAvailable)
            {
                _ = _logger.LogAsync($"[notification-register] {_notifications.LastError}");
            }
            _viewModel = _factory.Create(lifetime, dispatcherQueue, _notifications!);
            _window = new MainWindow(_viewModel, _tray);
            _window.ClosedByUser += OnWindowClosed;
            _window.ExitRequested += OnExitRequested;
            _showWindowWait = ThreadPool.RegisterWaitForSingleObject(
                _showWindowEvent,
                (_, timedOut) =>
                {
                    if (!timedOut)
                    {
                        dispatcherQueue.TryEnqueue(new DispatcherQueueHandler(() => _window?.ShowFromTray()));
                    }
                },
                null,
                Timeout.Infinite,
                executeOnlyOnce: false);
            var startMinimized = HasArgument(args.Arguments, "--minimized") || HasArgument(args.Arguments, "-m");
            if (startMinimized)
            {
                if (!_window.HideToTray())
                {
                    _window.Activate();
                }
            }
            else
            {
                _window.Activate();
            }

            _initializationTask = InitializeAsync(args.Arguments, _viewModel.ApplicationCancellationToken);
        }

        private async Task InitializeAsync(string commandLine, CancellationToken cancellationToken)
        {
            if (_viewModel is null || _window is null)
            {
                return;
            }

            try
            {
                await _viewModel.InitializeAsync(commandLine, cancellationToken);
                if (HasArgument(commandLine, "--startup") && _viewModel.IsRunning)
                {
                    const string message = "SpyYourDesktop 启动成功并开始监视窗口";
                    if (!(_notifications?.Show(message) ?? false))
                    {
                        _ = _logger.LogAsync($"[notification-startup] Windows notification failed: {_notifications?.LastError}");
                        _tray?.ShowNotification(message);
                    }
                    else
                    {
                        _ = _logger.LogAsync("[notification-startup] Windows notification sent.");
                    }
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }
            catch (Exception exception)
            {
                NativeMethods.ShowMessage($"应用初始化失败：{exception.Message}", "SpyYourDesktop");
            }
        }

        private static bool HasArgument(string commandLine, string expectedArgument) =>
            HasTextArgument(commandLine, expectedArgument) || HasProcessArgument(expectedArgument);

        private static bool HasProcessArgument(string expectedArgument) =>
            Environment.GetCommandLineArgs()
                .Skip(1)
                .Any(argument => string.Equals(argument, expectedArgument, StringComparison.OrdinalIgnoreCase));

        private static bool HasTextArgument(string commandLine, string expectedArgument) =>
            commandLine.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Any(argument => string.Equals(argument, expectedArgument, StringComparison.OrdinalIgnoreCase));

        private void OnWindowClosed(object? sender, EventArgs e)
        {
            _ = ShutdownAndExitAsync();
        }

        private void OnExitRequested(object? sender, EventArgs e)
        {
            _ = ShutdownAndCloseAsync();
        }

        private async Task ShutdownAndCloseAsync()
        {
            try
            {
                await ShutdownAsync();
                _window?.CloseForExit();
            }
            finally
            {
                Environment.Exit(0);
            }
        }

        private async Task ShutdownAndExitAsync()
        {
            try
            {
                await ShutdownAsync();
            }
            finally
            {
                Environment.Exit(0);
            }
        }

        private async Task ShutdownAsync()
        {
            if (_shutdownStarted)
            {
                return;
            }

            _shutdownStarted = true;
            try
            {
                _viewModel?.CancelPendingOperations();
                if (_initializationTask is not null && Task.CurrentId != _initializationTask.Id)
                {
                    try
                    {
                        await _initializationTask;
                    }
                    catch (OperationCanceledException)
                    {
                    }
                }

                if (_viewModel is not null)
                {
                    await _viewModel.DisposeAsync();
                }
            }
            finally
            {
                _tray?.Dispose();
                _notifications?.Dispose();
                _factory?.Dispose();
                _showWindowWait?.Unregister(null);
                _showWindowEvent.Dispose();
                try
                {
                    _instanceMutex.ReleaseMutex();
                }
                catch (ApplicationException)
                {
                }

                _instanceMutex.Dispose();
            }
        }
    }

    internal sealed class MainViewModelFactory(AppPaths paths, FileLogger logger) : IDisposable
    {
        private readonly HttpClient _httpClient = new()
        {
            Timeout = TimeSpan.FromSeconds(30)
        };

        public MainViewModel Create(
            IApplicationLifetime lifetime,
            DispatcherQueue dispatcherQueue,
            WindowsNotificationService notifications)
        {
            var configuration = new ConfigurationStore(paths);
            var foreground = new ForegroundWindowService();
            var ingest = new UsageIngestService(_httpClient);
            var monitoring = new MonitoringService(foreground, ingest, logger);
            var updates = new GitHubUpdateService(_httpClient, logger);
            return new MainViewModel(
                configuration,
                new StartupService(),
                monitoring,
                updates,
                logger,
                notifications,
                paths,
                lifetime,
                dispatcherQueue);
        }

        public void Dispose() => _httpClient.Dispose();
    }

    internal sealed class ApplicationLifetime(DispatcherQueue dispatcherQueue, Action requestExit) : IApplicationLifetime
    {
        public void RequestExit() => dispatcherQueue.TryEnqueue(new DispatcherQueueHandler(requestExit));
    }
}
