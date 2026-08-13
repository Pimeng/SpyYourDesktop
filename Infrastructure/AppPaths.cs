namespace Desktop.Infrastructure;

public sealed class AppPaths
{
    public const string DisplayName = "SpyYourDesktop";

    public AppPaths()
    {
        var dataRoot = AppContext.BaseDirectory;

        DataDirectory = dataRoot;
        LogDirectory = Path.Combine(dataRoot, "logs");
        ConfigFile = Path.Combine(AppContext.BaseDirectory, "config.json");
        LogFile = Path.Combine(LogDirectory, $"app-usage_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.log");
    }

    public string DataDirectory { get; }
    public string LogDirectory { get; }
    public string ConfigFile { get; }
    public string LogFile { get; }
}
