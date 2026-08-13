using Desktop.Infrastructure;
using Desktop.ViewModels;
using Desktop.Views;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using WinRT.Interop;

namespace Desktop;

public sealed partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;
    private readonly TrayService _tray;
    private readonly AppWindow _appWindow;
    private readonly DispatcherQueue _dispatcherQueue;
    private bool _forceClose;

    public MainWindow(MainViewModel viewModel, TrayService tray)
    {
        InitializeComponent();
        _viewModel = viewModel;
        _tray = tray;
        _dispatcherQueue = DispatcherQueue.GetForCurrentThread();
        _appWindow = GetAppWindow();
        ConfigureTitleBar();
        _tray.Attach(WindowNative.GetWindowHandle(this));

        RootFrame.Content = new MonitoringPage { DataContext = _viewModel };
        _appWindow.Resize(new Windows.Graphics.SizeInt32(1080, 760));
        _appWindow.Closing += OnAppWindowClosing;
        Closed += OnClosed;

        _tray.OpenRequested += (_, _) => RunOnUi(ShowFromTray);
        _tray.CheckUpdatesRequested += (_, _) => RunOnUi(() => _viewModel.CheckForUpdatesCommand.Execute(null));
        _tray.PrivacyToggleRequested += (_, _) => RunOnUi(() => _viewModel.PrivacyMode = !_viewModel.PrivacyMode);
        _tray.StartRequested += (_, _) => RunOnUi(() => _viewModel.StartCommand.Execute(null));
        _tray.StopRequested += (_, _) => RunOnUi(() => _viewModel.StopCommand.Execute(null));
        _tray.ExitRequested += (_, _) => RunOnUi(() => ExitRequested?.Invoke(this, EventArgs.Empty));

        _viewModel.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName is nameof(MainViewModel.IsRunning) or nameof(MainViewModel.PrivacyMode))
            {
                _tray.UpdateState(_viewModel.IsRunning, _viewModel.PrivacyMode);
            }
        };
    }

    public event EventHandler? ClosedByUser;
    public event EventHandler? ExitRequested;

    public void HideToTray()
    {
        _tray.UpdateState(_viewModel.IsRunning, _viewModel.PrivacyMode);
        _tray.Show(_viewModel.IsRunning);
        _appWindow.Hide();
    }

    public void CloseForExit()
    {
        _forceClose = true;
        _appWindow.Destroy();
    }

    private void OnAppWindowClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_forceClose)
        {
            return;
        }

        if (_viewModel.AllowBackground)
        {
            args.Cancel = true;
            HideToTray();
        }
    }

    private void ShowFromTray()
    {
        _tray.Hide();
        _appWindow.Show();
        Activate();
    }

    private void OnClosed(object sender, WindowEventArgs args)
    {
        _tray.Dispose();
        ClosedByUser?.Invoke(this, EventArgs.Empty);
    }

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

    private AppWindow GetAppWindow()
    {
        var windowHandle = WindowNative.GetWindowHandle(this);
        var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(windowHandle);
        return AppWindow.GetFromWindowId(windowId);
    }

    private void ConfigureTitleBar()
    {
        _appWindow.TitleBar.ExtendsContentIntoTitleBar = true;
        _appWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Tall;
        _appWindow.TitleBar.BackgroundColor = Colors.Transparent;
        _appWindow.TitleBar.InactiveBackgroundColor = Colors.Transparent;
        _appWindow.TitleBar.ButtonBackgroundColor = Colors.Transparent;
        _appWindow.TitleBar.ButtonInactiveBackgroundColor = Colors.Transparent;
        SetTitleBar(AppTitleBar);
    }
}
