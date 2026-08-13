namespace Desktop.Infrastructure;

public sealed class AppPaths
{
    public const string DisplayName = "SpyYourDesktop";

    public AppPaths()
    {
        var root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            DisplayName);

        DataDirectory = root;
        LogDirectory = Path.Combine(root, "logs");
        ConfigFile = Path.Combine(root, "config.json");
        LogFile = Path.Combine(LogDirectory, $"app-usage_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.log");
    }

    public string DataDirectory { get; }
    public string LogDirectory { get; }
    public string ConfigFile { get; }
    public string LogFile { get; }
    public string LegacyConfigFile => Path.Combine(AppContext.BaseDirectory, "config.json");
}
