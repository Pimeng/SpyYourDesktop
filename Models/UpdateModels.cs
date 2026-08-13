namespace Desktop.Models;

public sealed record UpdateRelease(
    string Tag,
    string HtmlUrl,
    string? ExecutableUrl,
    string Notes,
    Version Version);

public sealed record UpdateProgress(long DownloadedBytes, long? TotalBytes)
{
    public int Percent => TotalBytes is > 0
        ? (int)Math.Clamp(DownloadedBytes * 100 / TotalBytes.Value, 0L, 100L)
        : 0;
}
