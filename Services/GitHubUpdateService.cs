using System.Diagnostics;
using System.Net.Http.Headers;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using Desktop.Infrastructure;
using Desktop.Models;

namespace Desktop.Services;

public interface IUpdateService
{
    string CurrentVersion { get; }
    bool CanApplyInPlace { get; }
    Task<UpdateRelease?> CheckLatestAsync(CancellationToken cancellationToken);
    Task DownloadAndApplyAsync(UpdateRelease release, IProgress<UpdateProgress>? progress, CancellationToken cancellationToken);
    void OpenReleasePage(UpdateRelease release);
}

public sealed class GitHubUpdateService(HttpClient httpClient, IAppLogger logger) : IUpdateService
{
    private const string Owner = "BlueYeeeee";
    private const string Repository = "SpyYourDesktop";

    public string CurrentVersion => GetCurrentVersionString();

    public bool CanApplyInPlace =>
        IsSingleFile() &&
        !IsPackaged() &&
        !string.IsNullOrWhiteSpace(Environment.ProcessPath) &&
        File.Exists(Environment.ProcessPath);

    public async Task<UpdateRelease?> CheckLatestAsync(CancellationToken cancellationToken)
    {
        var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"https://api.github.com/repos/{Owner}/{Repository}/releases/latest");
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue(AppPaths.DisplayName, "1.0"));
        request.Headers.TryAddWithoutValidation("Accept", "application/vnd.github+json");

        using (request)
        using (var response = await httpClient.SendAsync(request, cancellationToken))
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            response.EnsureSuccessStatusCode();

            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            var tag = GetString(root, "tag_name");
            if (string.IsNullOrWhiteSpace(tag))
            {
                return null;
            }

            var executableUrl = FindExecutableUrl(root);
            return new UpdateRelease(
                tag,
                GetString(root, "html_url") ?? string.Empty,
                executableUrl,
                GetString(root, "body")?.Trim() ?? string.Empty,
                ParseVersion(tag));
        }
    }

    public async Task DownloadAndApplyAsync(
        UpdateRelease release,
        IProgress<UpdateProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (!CanApplyInPlace || string.IsNullOrWhiteSpace(release.ExecutableUrl))
        {
            OpenReleasePage(release);
            return;
        }

        var executablePath = Environment.ProcessPath!;
        var temporaryPath = executablePath + ".new";
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, release.ExecutableUrl);
            request.Headers.UserAgent.Add(new ProductInfoHeaderValue(AppPaths.DisplayName, "1.0"));
            using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();

            var total = response.Content.Headers.ContentLength;
            await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
            await using var destination = new FileStream(temporaryPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true);
            var buffer = new byte[81920];
            long downloaded = 0;
            int read;
            while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
            {
                await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                downloaded += read;
                progress?.Report(new UpdateProgress(downloaded, total));
            }

            await destination.FlushAsync(cancellationToken);
            var scriptPath = Path.Combine(Path.GetTempPath(), $"{AppPaths.DisplayName}_update_{Guid.NewGuid():N}.cmd");
            var script = BuildUpdateScript(executablePath, temporaryPath);
            await File.WriteAllTextAsync(scriptPath, script, cancellationToken);

            Process.Start(new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c \"{scriptPath}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(executablePath)!
            });
            await logger.LogAsync($"[update] downloaded {release.Tag}; requesting restart", cancellationToken);
        }
        catch
        {
            try
            {
                if (File.Exists(temporaryPath))
                {
                    File.Delete(temporaryPath);
                }
            }
            catch
            {
            }

            throw;
        }
    }

    public void OpenReleasePage(UpdateRelease release)
    {
        if (string.IsNullOrWhiteSpace(release.HtmlUrl))
        {
            return;
        }

        Process.Start(new ProcessStartInfo
        {
            FileName = release.HtmlUrl,
            UseShellExecute = true
        });
    }

    private static string BuildUpdateScript(string executablePath, string temporaryPath)
    {
        var pid = Environment.ProcessId;
        return $"@echo off{Environment.NewLine}" +
               $":wait{Environment.NewLine}" +
               $"tasklist /FI \"PID eq {pid}\" | find \"{pid}\" >nul{Environment.NewLine}" +
               $"if not errorlevel 1 (timeout /t 1 /nobreak >nul & goto wait){Environment.NewLine}" +
               $"move /y \"{temporaryPath}\" \"{executablePath}\" >nul{Environment.NewLine}" +
               $"start \"\" \"{executablePath}\"{Environment.NewLine}" +
               "del \"%~f0\"";
    }

    private static string? GetString(JsonElement root, string propertyName) =>
        root.TryGetProperty(propertyName, out var element) && element.ValueKind == JsonValueKind.String
            ? element.GetString()
            : null;

    private static string? FindExecutableUrl(JsonElement root)
    {
        if (!root.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var candidates = assets.EnumerateArray()
            .Select(asset => GetString(asset, "browser_download_url"))
            .Where(url => url?.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) == true)
            .Cast<string>()
            .ToArray();
        if (candidates.Length == 0)
        {
            return null;
        }

        var architecture = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X86 => "win-x86",
            Architecture.Arm64 => "win-arm64",
            _ => "win-x64"
        };
        var matching = candidates.FirstOrDefault(url =>
            url.Contains(architecture, StringComparison.OrdinalIgnoreCase) ||
            url.Contains(architecture[4..], StringComparison.OrdinalIgnoreCase));
        return matching ?? (candidates.Length == 1 ? candidates[0] : null);
    }

    private static string GetCurrentVersionString()
    {
        var version = Assembly.GetEntryAssembly()?
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion;
        return string.IsNullOrWhiteSpace(version) ? "1.0.0.0" : version;
    }

    private static Version ParseVersion(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return new Version(0, 0, 0, 0);
        }

        var normalized = value.Trim();
        if (normalized.StartsWith("v", StringComparison.OrdinalIgnoreCase))
        {
            normalized = normalized[1..];
        }

        var separator = normalized.IndexOfAny(['-', '+']);
        if (separator >= 0)
        {
            normalized = normalized[..separator];
        }

        return Version.TryParse(normalized, out var version)
            ? version
            : new Version(0, 0, 0, 0);
    }

    private static bool IsPackaged()
    {
        try
        {
            return !string.IsNullOrWhiteSpace(Windows.ApplicationModel.Package.Current.Id.Name);
        }
        catch
        {
            return false;
        }
    }

    private static bool IsSingleFile() =>
        string.IsNullOrEmpty(typeof(GitHubUpdateService).Assembly.Location);
}
