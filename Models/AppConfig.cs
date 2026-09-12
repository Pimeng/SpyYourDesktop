using System.Text.Json.Serialization;

namespace Desktop.Models;

public enum StartupMode
{
    Disabled,
    Silent,
    Visible
}

public sealed class AppConfig
{
    [JsonPropertyName("serverUrl")]
    public string ServerUrl { get; set; } = "http://127.0.0.1:3000/api/ingest";

    [JsonPropertyName("intervalSec")]
    public int IntervalSeconds { get; set; } = 5;

    [JsonPropertyName("heartbeatSec")]
    public int HeartbeatSeconds { get; set; } = 10;

    [JsonPropertyName("machineId")]
    public string? MachineId { get; set; }

    [JsonPropertyName("uploadKey")]
    public string? UploadKey { get; set; }

    [JsonPropertyName("showKey")]
    public bool ShowKey { get; set; } = true;

    [JsonPropertyName("startupMode")]
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public StartupMode? StartupMode { get; set; }

    [JsonPropertyName("autoStart")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? AutoStartLegacy { get; set; }

    [JsonPropertyName("allowBackground")]
    public bool AllowBackground { get; set; }

    [JsonPropertyName("startHidden")]
    public bool? StartHiddenLegacy { get; set; }

    [JsonPropertyName("skipVersion")]
    public string? SkippedVersion { get; set; }

    [JsonPropertyName("forceAllowLongTitle")]
    public bool ForceAllowLongTitle { get; set; }

    /// <summary>
    /// 是否读取系统媒体（SMTC）信息并随活动记录一起上报。默认关闭。
    /// </summary>
    [JsonPropertyName("reportMedia")]
    public bool ReportMedia { get; set; }
}
