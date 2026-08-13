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
        private readonly Mutex _instanceMutex;
        private readonly AppPaths _paths = null!;
        private readonly FileLogger _logger = null!;
        private readonly MainViewModelFactory? _factory;
        private MainWindow? _window;
        private MainViewModel? _viewModel;
        private TrayService? _tray;
        private Task? _initializationTask;
        private bool _shutdownStarted;

        /// <summary>
        /// Initializes the singleton application object.  This is the first line of authored code
        /// executed, and as such is the logical equivalent of main() or WinMain().
        /// </summary>
        public App()
        {
            InitializeComponent();

            _instanceMutex = new Mutex(true, "SpyYourDesktop_Singleton", out var createdNew);
            if (!createdNew)
            {
                NativeMethods.ShowMessage("应用已在运行。", "SpyYourDesktop");
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
                return;
            }

            if (_window is not null)
            {
                _window.Activate();
                return;
            }

            var dispatcherQueue = DispatcherQueue.GetForCurrentThread();
            var lifetime = new ApplicationLifetime(dispatcherQueue, () => _ = ShutdownAndCloseAsync());
            _viewModel = _factory.Create(lifetime, dispatcherQueue);
            _tray = new TrayService();
            _window = new MainWindow(_viewModel, _tray);
            _window.ClosedByUser += OnWindowClosed;
            _window.ExitRequested += OnExitRequested;
            _window.Activate();
            if (HasMinimizedArgument(args.Arguments))
            {
                _window.HideToTray();
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
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }
            catch (Exception exception)
            {
                NativeMethods.ShowMessage($"应用初始化失败：{exception.Message}", "SpyYourDesktop");
            }
        }

        private static bool HasMinimizedArgument(string commandLine) =>
            commandLine.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Any(argument =>
                    string.Equals(argument, "--minimized", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(argument, "-m", StringComparison.OrdinalIgnoreCase));

        private void OnWindowClosed(object? sender, EventArgs e)
        {
            _ = ShutdownAsync();
        }

        private void OnExitRequested(object? sender, EventArgs e)
        {
            _ = ShutdownAndCloseAsync();
        }

        private async Task ShutdownAndCloseAsync()
        {
            await ShutdownAsync();
            _window?.CloseForExit();
        }

        private async Task ShutdownAsync()
        {
            if (_shutdownStarted)
            {
                return;
            }

            _shutdownStarted = true;
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

            _tray?.Dispose();
            _factory?.Dispose();
            _instanceMutex.ReleaseMutex();
            _instanceMutex.Dispose();
        }
    }

    internal sealed class MainViewModelFactory(AppPaths paths, FileLogger logger) : IDisposable
    {
        private readonly HttpClient _httpClient = new()
        {
            Timeout = TimeSpan.FromSeconds(30)
        };

        public MainViewModel Create(IApplicationLifetime lifetime, DispatcherQueue dispatcherQueue)
        {
            var configuration = new ConfigurationStore(paths);
            var foreground = new ForegroundWindowService();
            var ingest = new UsageIngestService(_httpClient);
            var monitoring = new MonitoringService(foreground, ingest, logger);
            var updates = new GitHubUpdateService(_httpClient, logger);
            return new MainViewModel(configuration, new StartupService(), monitoring, updates, logger, paths, lifetime, dispatcherQueue);
        }

        public void Dispose() => _httpClient.Dispose();
    }

    internal sealed class ApplicationLifetime(DispatcherQueue dispatcherQueue, Action requestExit) : IApplicationLifetime
    {
        public void RequestExit() => dispatcherQueue.TryEnqueue(new DispatcherQueueHandler(requestExit));
    }
}
