namespace Desktop.Infrastructure;

public sealed class WindowsNotificationService : IDisposable
{
    private bool _isRegistered;

    public bool IsAvailable => _isRegistered;
    public string LastError { get; private set; } = string.Empty;

    public void Register()
    {
        if (_isRegistered)
        {
            return;
        }

        try
        {
            WindowsToastNotification.RegisterShortcut();
            _isRegistered = true;
            LastError = string.Empty;
        }
        catch (Exception exception)
        {
            _isRegistered = false;
            LastError = $"{exception.GetType().Name}: {exception.Message}";
        }
    }

    public bool Show(string message)
    {
        if (!IsAvailable)
        {
            if (string.IsNullOrWhiteSpace(LastError))
            {
                LastError = "Windows 通知服务尚未注册。";
            }

            return false;
        }

        try
        {
            WindowsToastNotification.Show(AppPaths.DisplayName, message);
            LastError = string.Empty;
            return true;
        }
        catch (Exception exception)
        {
            LastError = $"{exception.GetType().Name}: {exception.Message}";
            return false;
        }
    }

    public void Dispose()
    {
        if (!_isRegistered)
        {
            return;
        }

        try
        {
            WindowsToastNotification.UnregisterShortcut();
        }
        catch
        {
        }
        finally
        {
            _isRegistered = false;
        }
    }
}
