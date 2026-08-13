using System.Text;

namespace Desktop.Infrastructure;

public interface IAppLogger
{
    Task LogAsync(string message, CancellationToken cancellationToken = default);
}

public sealed class FileLogger(AppPaths paths) : IAppLogger, IAsyncDisposable
{
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private bool _disposed;

    public async Task LogAsync(string message, CancellationToken cancellationToken = default)
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            await _writeLock.WaitAsync(cancellationToken);
            try
            {
                Directory.CreateDirectory(paths.LogDirectory);
                var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {message}{Environment.NewLine}";
                await File.AppendAllTextAsync(paths.LogFile, line, Encoding.UTF8, cancellationToken);
            }
            finally
            {
                _writeLock.Release();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    public async ValueTask DisposeAsync()
    {
        _disposed = true;
        await _writeLock.WaitAsync();
        _writeLock.Release();
        _writeLock.Dispose();
    }
}
