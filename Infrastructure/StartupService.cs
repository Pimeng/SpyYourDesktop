using Microsoft.Win32;
using Desktop.Models;

namespace Desktop.Infrastructure;

public interface IStartupService
{
    StartupMode GetMode();
    void SetMode(StartupMode mode);
}

public sealed class StartupService : IStartupService
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public StartupMode GetMode()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, false);
            var current = key?.GetValue(AppPaths.DisplayName);
            var legacy = key?.GetValue(GetLegacyValueName());
            return GetModeFromCommand(current) ?? GetModeFromCommand(legacy) ?? StartupMode.Disabled;
        }
        catch (System.Security.SecurityException)
        {
            return StartupMode.Disabled;
        }
        catch (UnauthorizedAccessException)
        {
            return StartupMode.Disabled;
        }
    }

    public void SetMode(StartupMode mode)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, true)
            ?? Registry.CurrentUser.CreateSubKey(RunKey, true)
            ?? throw new InvalidOperationException("Unable to open the Windows startup registry key.");

        if (mode is not StartupMode.Disabled)
        {
            var arguments = mode is StartupMode.Silent
                ? " --startup --minimized"
                : " --startup";
            key.SetValue(AppPaths.DisplayName, $"\"{GetExecutablePath()}\"{arguments}");
            var legacyName = GetLegacyValueName();
            if (!string.Equals(legacyName, AppPaths.DisplayName, StringComparison.OrdinalIgnoreCase))
            {
                key.DeleteValue(legacyName, false);
            }
        }
        else
        {
            key.DeleteValue(AppPaths.DisplayName, false);
            key.DeleteValue(GetLegacyValueName(), false);
        }
    }

    private static StartupMode? GetModeFromCommand(object? value)
    {
        if (value is not string command || !command.Contains(GetExecutablePath(), StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return command.Contains("--minimized", StringComparison.OrdinalIgnoreCase)
            ? StartupMode.Silent
            : StartupMode.Visible;
    }

    private static string GetLegacyValueName() =>
        Path.GetFileNameWithoutExtension(GetExecutablePath());

    private static string GetExecutablePath() =>
        Environment.ProcessPath ?? Environment.GetCommandLineArgs()[0];
}
