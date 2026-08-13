using System.Diagnostics;
using System.Text;
using Desktop.Infrastructure;
using Desktop.Models;

namespace Desktop.Services;

public interface IForegroundWindowService
{
    ForegroundWindowSnapshot ReadCurrent();
}

public sealed class ForegroundWindowService : IForegroundWindowService
{
    public ForegroundWindowSnapshot ReadCurrent()
    {
        try
        {
            var handle = NativeMethods.GetForegroundWindow();
            if (handle == IntPtr.Zero)
            {
                return new ForegroundWindowSnapshot(string.Empty, string.Empty, 0);
            }

            var titleBuffer = new StringBuilder(1024);
            NativeMethods.GetWindowText(handle, titleBuffer, titleBuffer.Capacity);
            NativeMethods.GetWindowThreadProcessId(handle, out var processId);

            var application = string.Empty;
            try
            {
                using var process = Process.GetProcessById((int)processId);
                application = Path.GetFileNameWithoutExtension(process.MainModule?.FileName ?? process.ProcessName);
            }
            catch
            {
                // Access to another process can be denied even when the foreground window is readable.
            }

            return new ForegroundWindowSnapshot(
                Sanitize(titleBuffer.ToString()),
                Sanitize(application),
                (int)processId);
        }
        catch
        {
            return new ForegroundWindowSnapshot(string.Empty, string.Empty, 0);
        }
    }

    private static string Sanitize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var result = value.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return result.Length > 512 ? result[..512] : result;
    }
}
