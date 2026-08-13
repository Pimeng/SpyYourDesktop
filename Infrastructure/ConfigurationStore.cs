using System.Text;
using System.Text.Json;
using Desktop.Models;

namespace Desktop.Infrastructure;

public interface IConfigurationStore
{
    bool LastLoadSucceeded { get; }
    Task<AppConfig> LoadAsync(CancellationToken cancellationToken = default);
    Task SaveAsync(AppConfig config, CancellationToken cancellationToken = default);
}

public sealed class ConfigurationStore(AppPaths paths) : IConfigurationStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private bool _lastLoadSucceeded = true;

    public bool LastLoadSucceeded => _lastLoadSucceeded;

    public async Task<AppConfig> LoadAsync(CancellationToken cancellationToken = default)
    {
        var source = File.Exists(paths.ConfigFile)
            ? paths.ConfigFile
            : paths.LegacyConfigFile;

        if (!File.Exists(source))
        {
            _lastLoadSucceeded = true;
            return new AppConfig();
        }

        try
        {
            await using var stream = File.OpenRead(source);
            var config = await JsonSerializer.DeserializeAsync<AppConfig>(stream, JsonOptions, cancellationToken);
            config ??= new AppConfig();
            _lastLoadSucceeded = true;

            if (config.StartHiddenLegacy == true)
            {
                config.AllowBackground = true;
            }

            return config;
        }
        catch (JsonException)
        {
            _lastLoadSucceeded = false;
            return new AppConfig();
        }
        catch (IOException)
        {
            _lastLoadSucceeded = false;
            return new AppConfig();
        }
        catch (UnauthorizedAccessException)
        {
            _lastLoadSucceeded = false;
            return new AppConfig();
        }
    }

    public async Task SaveAsync(AppConfig config, CancellationToken cancellationToken = default)
    {
        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            Directory.CreateDirectory(paths.DataDirectory);
            var tempFile = paths.ConfigFile + ".tmp";
            var json = JsonSerializer.Serialize(config, JsonOptions);

            await File.WriteAllTextAsync(tempFile, json, Encoding.UTF8, cancellationToken);
            File.Move(tempFile, paths.ConfigFile, true);
            _lastLoadSucceeded = true;
        }
        finally
        {
            _writeLock.Release();
        }
    }
}
