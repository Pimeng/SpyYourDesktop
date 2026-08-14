using System.Text.Json.Serialization;

namespace Desktop.Models;

public sealed record MonitorSettings(
    string ServerUrl,
    int IntervalSeconds,
    int HeartbeatSeconds,
    string MachineId,
    string UploadKey,
    bool PrivacyMode,
    bool ForceAllowLongTitle);

public sealed record ForegroundWindowSnapshot(string Title, string Application, int ProcessId);

public sealed class UploadEvent
{
    [JsonPropertyName("machine")]
    public string Machine { get; init; } = string.Empty;

    [JsonPropertyName("window_title")]
    public string WindowTitle { get; init; } = string.Empty;

    [JsonPropertyName("app")]
    public string Application { get; init; } = string.Empty;

    [JsonPropertyName("raw")]
    public RawUploadInfo Raw { get; init; } = new();

    [JsonPropertyName("app_version")]
    public string AppVersion { get; set; } = string.Empty;

    [JsonPropertyName("os")]
    public string Os { get; set; } = "Windows";
}

public sealed class RawUploadInfo
{
    [JsonPropertyName("exe")]
    public string Exe { get; init; } = string.Empty;

    [JsonPropertyName("pid")]
    public int ProcessId { get; init; }

    [JsonPropertyName("reason")]
    public string Reason { get; init; } = string.Empty;
}

public sealed class MonitoringStatusChangedEventArgs(bool isRunning) : EventArgs
{
    public bool IsRunning { get; } = isRunning;
}

public sealed class UsageSentEventArgs(DateTimeOffset sentAt, string application, string title) : EventArgs
{
    public DateTimeOffset SentAt { get; } = sentAt;
    public string Application { get; } = application;
    public string Title { get; } = title;
}

public sealed class MonitoringErrorEventArgs(string message, bool stopsMonitoring, int? statusCode = null) : EventArgs
{
    public string Message { get; } = message;
    public bool StopsMonitoring { get; } = stopsMonitoring;
    public int? StatusCode { get; } = statusCode;
}

public sealed class IngestErrorException(
    string message,
    int statusCode,
    string? serverError,
    string rawBody) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
    public string? ServerError { get; } = serverError;
    public string RawBody { get; } = rawBody;
}
