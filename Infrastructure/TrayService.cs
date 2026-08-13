using System.Runtime.InteropServices;

namespace Desktop.Infrastructure;

public sealed class TrayService : IDisposable
{
    private static readonly UIntPtr SubclassId = new(1);
    private const uint NotifyAdd = 0x00000000;
    private const uint NotifyModify = 0x00000001;
    private const uint NotifyDelete = 0x00000002;
    private const uint NotifyMessage = 0x00000001;
    private const uint NotifyIcon = 0x00000002;
    private const uint NotifyTip = 0x00000004;
    private const uint NotifyInfo = 0x00000010;
    private const uint InfoIcon = 0x00000001;
    private const uint CallbackMessage = 0x8001;
    private const uint SizeMessage = 0x0005;
    private const uint SizeMinimized = 1;
    private const uint WindowMessage = 0x0111;
    private const uint LeftButtonDoubleClick = 0x0203;
    private const uint RightButtonUp = 0x0205;
    private const uint MenuOpen = 1001;
    private const uint MenuCheckUpdates = 1002;
    private const uint MenuPrivacy = 1003;
    private const uint MenuStart = 1004;
    private const uint MenuStop = 1005;
    private const uint MenuExit = 1006;
    private const uint MenuString = 0x00000000;
    private const uint MenuSeparator = 0x00000800;
    private const uint MenuEnabled = 0x00000000;
    private const uint MenuGrayed = 0x00000001;
    private const uint MenuChecked = 0x00000008;
    private const uint TrackMenuRightButton = 0x0002;
    private const uint TrackMenuReturnCommand = 0x0100;

    private readonly SubclassProc _subclassProc;
    private IntPtr _windowHandle;
    private IntPtr _menuHandle;
    private IntPtr _iconHandle;
    private bool _isVisible;
    private bool _disposed;

    public TrayService()
    {
        _subclassProc = WindowSubclassProc;
    }

    public event EventHandler? OpenRequested;
    public event EventHandler? CheckUpdatesRequested;
    public event EventHandler? PrivacyToggleRequested;
    public event EventHandler? StartRequested;
    public event EventHandler? StopRequested;
    public event EventHandler? ExitRequested;
    public event EventHandler? MinimizeRequested;

    public void Attach(IntPtr windowHandle)
    {
        if (_disposed || windowHandle == IntPtr.Zero || _windowHandle != IntPtr.Zero)
        {
            return;
        }

        _windowHandle = windowHandle;
        _menuHandle = CreatePopupMenu();
        AppendMenu(_menuHandle, MenuString, MenuOpen, "打开主界面");
        AppendMenu(_menuHandle, MenuString, MenuCheckUpdates, "检查更新");
        AppendMenu(_menuHandle, MenuString, MenuPrivacy, "隐私模式（不采集标题/应用）");
        AppendMenu(_menuHandle, MenuString, MenuStart, "开始监控");
        AppendMenu(_menuHandle, MenuString, MenuStop, "停止监控");
        AppendMenu(_menuHandle, MenuSeparator, 0, string.Empty);
        AppendMenu(_menuHandle, MenuString, MenuExit, "退出");
        SetWindowSubclass(_windowHandle, _subclassProc, SubclassId, UIntPtr.Zero);
    }

    public void UpdateState(bool isRunning, bool privacyMode)
    {
        if (_disposed || _menuHandle == IntPtr.Zero)
        {
            return;
        }

        EnableMenuItem(_menuHandle, MenuStart, isRunning ? MenuGrayed : MenuEnabled);
        EnableMenuItem(_menuHandle, MenuStop, isRunning ? MenuEnabled : MenuGrayed);
        CheckMenuItem(_menuHandle, MenuPrivacy, privacyMode ? MenuChecked : MenuEnabled);
    }

    public void Show(bool isRunning)
    {
        if (_disposed || _windowHandle == IntPtr.Zero)
        {
            return;
        }

        var data = CreateNotifyData();
        if (!_isVisible)
        {
            Shell_NotifyIcon(NotifyAdd, ref data);
            _isVisible = true;
        }

        data.uFlags = NotifyTip;
        data.szTip = isRunning ? "SpyYourDesktop（运行中）" : "SpyYourDesktop";
        Shell_NotifyIcon(NotifyModify, ref data);
    }

    public void Hide()
    {
        if (!_isVisible)
        {
            return;
        }

        var data = CreateNotifyData();
        Shell_NotifyIcon(NotifyDelete, ref data);
        _isVisible = false;
    }

    public void ShowNotification(string message)
    {
        if (_disposed || _windowHandle == IntPtr.Zero)
        {
            return;
        }

        if (!_isVisible)
        {
            Show(isRunning: true);
        }

        var data = CreateNotifyData();
        data.uFlags = NotifyInfo;
        data.szInfo = message;
        data.szInfoTitle = AppPaths.DisplayName;
        data.dwInfoFlags = InfoIcon;
        data.uTimeoutOrVersion = 5000;
        Shell_NotifyIcon(NotifyModify, ref data);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        Hide();
        _disposed = true;
        if (_windowHandle != IntPtr.Zero)
        {
            RemoveWindowSubclass(_windowHandle, _subclassProc, SubclassId);
        }

        if (_menuHandle != IntPtr.Zero)
        {
            DestroyMenu(_menuHandle);
        }

        if (_iconHandle != IntPtr.Zero)
        {
            DestroyIcon(_iconHandle);
            _iconHandle = IntPtr.Zero;
        }
    }

    private NOTIFYICONDATA CreateNotifyData() => new()
    {
        cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATA>(),
        hWnd = _windowHandle,
        uID = 1,
        uFlags = NotifyMessage | NotifyIcon | NotifyTip,
        uCallbackMessage = CallbackMessage,
        hIcon = GetApplicationIcon(),
        szTip = "SpyYourDesktop"
    };

    private IntPtr GetApplicationIcon()
    {
        if (_iconHandle != IntPtr.Zero)
        {
            return _iconHandle;
        }

        var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "App.ico");
        _iconHandle = LoadImage(IntPtr.Zero, iconPath, ImageIcon, 0, 0, LoadFromFile | DefaultSize);
        return _iconHandle;
    }

    private IntPtr WindowSubclassProc(
        IntPtr hWnd,
        uint message,
        IntPtr wParam,
        IntPtr lParam,
        UIntPtr subclassId,
        UIntPtr referenceData)
    {
        if (message == CallbackMessage)
        {
            var shellMessage = unchecked((uint)lParam.ToInt64());
            if (shellMessage == LeftButtonDoubleClick)
            {
                OpenRequested?.Invoke(this, EventArgs.Empty);
            }
            else if (shellMessage == RightButtonUp)
            {
                ShowContextMenu();
            }
        }
        else if (message == WindowMessage)
        {
            var command = unchecked((uint)wParam.ToInt64() & 0xFFFF);
            DispatchCommand(command);
        }
        else if (message == SizeMessage)
        {
            var sizeType = unchecked((uint)wParam.ToInt64()) & 0xFFFF;
            if (sizeType == SizeMinimized)
            {
                MinimizeRequested?.Invoke(this, EventArgs.Empty);
            }
        }

        return DefSubclassProc(hWnd, message, wParam, lParam);
    }

    private void ShowContextMenu()
    {
        if (_menuHandle == IntPtr.Zero)
        {
            return;
        }

        GetCursorPos(out var point);
        SetForegroundWindow(_windowHandle);
        var command = TrackPopupMenu(
            _menuHandle,
            TrackMenuRightButton | TrackMenuReturnCommand,
            point.X,
            point.Y,
            0,
            _windowHandle,
            IntPtr.Zero);
        DispatchCommand(command);
        PostMessage(_windowHandle, 0, IntPtr.Zero, IntPtr.Zero);
    }

    private void DispatchCommand(uint command)
    {
        switch (command)
        {
            case MenuOpen:
                OpenRequested?.Invoke(this, EventArgs.Empty);
                break;
            case MenuCheckUpdates:
                CheckUpdatesRequested?.Invoke(this, EventArgs.Empty);
                break;
            case MenuPrivacy:
                PrivacyToggleRequested?.Invoke(this, EventArgs.Empty);
                break;
            case MenuStart:
                StartRequested?.Invoke(this, EventArgs.Empty);
                break;
            case MenuStop:
                StopRequested?.Invoke(this, EventArgs.Empty);
                break;
            case MenuExit:
                ExitRequested?.Invoke(this, EventArgs.Empty);
                break;
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATA
    {
        public uint cbSize;
        public IntPtr hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szTip;
        public uint dwState;
        public uint dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string szInfo;
        public uint uTimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string szInfoTitle;
        public uint dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate IntPtr SubclassProc(IntPtr hWnd, uint message, IntPtr wParam, IntPtr lParam, UIntPtr subclassId, UIntPtr referenceData);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Shell_NotifyIcon(uint message, ref NOTIFYICONDATA data);

    private const uint ImageIcon = 1;
    private const uint LoadFromFile = 0x00000010;
    private const uint DefaultSize = 0x00000040;

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr LoadImage(IntPtr instance, string name, uint type, int width, int height, uint loadFlags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr icon);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool AppendMenu(IntPtr menu, uint flags, uint newItem, string newItemText);

    [DllImport("user32.dll")]
    private static extern bool EnableMenuItem(IntPtr menu, uint item, uint enable);

    [DllImport("user32.dll")]
    private static extern uint CheckMenuItem(IntPtr menu, uint item, uint check);

    [DllImport("user32.dll")]
    private static extern bool DestroyMenu(IntPtr menu);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT point);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr window);

    [DllImport("user32.dll")]
    private static extern uint TrackPopupMenu(IntPtr menu, uint flags, int x, int y, int reserved, IntPtr owner, IntPtr rectangle);

    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("comctl32.dll", SetLastError = true)]
    private static extern bool SetWindowSubclass(IntPtr window, SubclassProc callback, UIntPtr subclassId, UIntPtr referenceData);

    [DllImport("comctl32.dll", SetLastError = true)]
    private static extern bool RemoveWindowSubclass(IntPtr window, SubclassProc callback, UIntPtr subclassId);

    [DllImport("comctl32.dll")]
    private static extern IntPtr DefSubclassProc(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
}
