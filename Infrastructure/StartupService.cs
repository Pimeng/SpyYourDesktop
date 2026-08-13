using Microsoft.Win32;

namespace Desktop.Infrastructure;

public interface IStartupService
{
    bool IsEnabled();
    void SetEnabled(bool enabled);
}

public sealed class StartupService : IStartupService
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, false);
            var current = key?.GetValue(AppPaths.DisplayName);
            var legacy = key?.GetValue(GetLegacyValueName());
            return IsMatchingCommand(current) || IsMatchingCommand(legacy);
        }
        catch (System.Security.SecurityException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    public void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, true)
            ?? Registry.CurrentUser.CreateSubKey(RunKey, true)
            ?? throw new InvalidOperationException("Unable to open the Windows startup registry key.");

        if (enabled)
        {
            key.SetValue(AppPaths.DisplayName, $"\"{GetExecutablePath()}\"");
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

    private static bool IsMatchingCommand(object? value) =>
        value is string command && command.Contains(GetExecutablePath(), StringComparison.OrdinalIgnoreCase);

    private static string GetLegacyValueName() =>
        Path.GetFileNameWithoutExtension(GetExecutablePath());

    private static string GetExecutablePath() =>
        Environment.ProcessPath ?? Environment.GetCommandLineArgs()[0];
}
