using System.Runtime.InteropServices;
using System.Text;

namespace Desktop.Infrastructure;

internal static class NativeMethods
{
    internal const int RestoreWindow = 9;

    [DllImport("user32.dll")]
    internal static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    internal static extern int GetWindowText(IntPtr windowHandle, StringBuilder text, int maxLength);

    [DllImport("user32.dll")]
    internal static extern uint GetWindowThreadProcessId(IntPtr windowHandle, out uint processId);

    [DllImport("user32.dll")]
    internal static extern bool ShowWindow(IntPtr windowHandle, int command);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBox(IntPtr hWnd, string text, string caption, uint type);

    internal static void ShowMessage(string message, string caption) => MessageBox(IntPtr.Zero, message, caption, 0x40);
}
