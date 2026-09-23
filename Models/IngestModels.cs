using System.Text.Json;
using System.Text.Json.Serialization;

namespace Desktop.Models;

/// <summary>
/// SpyYourDesktop 上报协议 v2 的常量与枚举值。
/// 这些都是与服务端之间的稳定契约，改动等同于破坏性变更。
/// </summary>
public static class IngestProtocol
{
    public const int Version = 2;

    public const string ClientName = "spyyourdesktop";

    /// <summary>媒体空闲超时默认值（毫秒）：非播放态的媒体会话持续该时长后视为没有当前媒体。</summary>
    public const int DefaultMediaIdleTimeoutMs = 180_000;

    /// <summary>事件类型。新增类型是非破坏性扩展。</summary>
    public static class EventTypes
    {
        public const string WindowActivity = "window.activity";
        public const string MediaPlayback = "media.playback";
    }

    /// <summary>事件被发送的原因。</summary>
    public static class Triggers
    {
        public const string Startup = "startup";
        public const string Change = "change";
        public const string Media = "media";
        public const string Heartbeat = "heartbeat";
        public const string Manual = "manual";
    }

    /// <summary>
    /// 错误码。客户端只根据错误码分支，绝不匹配 message 文本。
    /// </summary>
    public static class ErrorCodes
    {
        // 请求级
        public const string Unauthorized = "UNAUTHORIZED";
        public const string Forbidden = "FORBIDDEN";
        public const string RateLimited = "RATE_LIMITED";
        public const string ProtocolUnsupported = "PROTOCOL_UNSUPPORTED";
        public const string PayloadTooLarge = "PAYLOAD_TOO_LARGE";
        public const string MalformedRequest = "MALFORMED_REQUEST";
        public const string ServerError = "SERVER_ERROR";

        // 事件级
        public const string UnsupportedType = "UNSUPPORTED_TYPE";
        public const string FieldTooLong = "FIELD_TOO_LONG";
        public const string InvalidField = "INVALID_FIELD";
        public const string Duplicate = "DUPLICATE";
    }
}

/// <summary>
/// 协议时间：统一 UTC、毫秒精度。序列化为带 Z 的 ISO 8601 字符串。
/// </summary>
public static class ProtocolTime
{
    public static DateTime UtcNow() => Truncate(DateTime.UtcNow);

    public static DateTime ToUtc(DateTimeOffset value) => Truncate(value.UtcDateTime);

    public static DateTime Truncate(DateTime value)
    {
        var utc = value.Kind == DateTimeKind.Utc ? value : value.ToUniversalTime();
        return new DateTime(utc.Ticks - (utc.Ticks % TimeSpan.TicksPerMillisecond), DateTimeKind.Utc);
    }
}

#region 请求

public sealed class IngestRequest
{
    [JsonPropertyName("protocol_version")]
    public int ProtocolVersion { get; init; } = IngestProtocol.Version;

    [JsonPropertyName("client")]
    public IngestClientInfo Client { get; init; } = new();

    [JsonPropertyName("device")]
    public IngestDeviceInfo Device { get; init; } = new();

    [JsonPropertyName("session")]
    public IngestSessionInfo Session { get; init; } = new();

    [JsonPropertyName("sent_at")]
    public DateTime SentAt { get; init; }

    [JsonPropertyName("policy")]
    public IngestPolicy Policy { get; init; } = new();

    /// <summary>本批次的事件。空数组是合法的心跳。</summary>
    [JsonPropertyName("events")]
    public IReadOnlyList<IngestEvent> Events { get; init; } = [];
}

public sealed class IngestClientInfo
{
    [JsonPropertyName("name")]
    public string Name { get; init; } = IngestProtocol.ClientName;

    [JsonPropertyName("version")]
    public string Version { get; init; } = string.Empty;

    [JsonPropertyName("platform")]
    public string Platform { get; init; } = "windows";

    [JsonPropertyName("os_version")]
    public string OsVersion { get; init; } = string.Empty;

    /// <summary>客户端声明自己能产生的事件类型。</summary>
    [JsonPropertyName("capabilities")]
    public IReadOnlyList<string> Capabilities { get; init; } = [];
}

public sealed class IngestDeviceInfo
{
    [JsonPropertyName("id")]
    public string Id { get; init; } = string.Empty;
}

public sealed class IngestSessionInfo
{
    [JsonPropertyName("id")]
    public string Id { get; init; } = string.Empty;

    [JsonPropertyName("started_at")]
    public DateTime StartedAt { get; init; }
}

/// <summary>
/// 采样策略。每次请求都带上，服务端才能区分“没人用”和“没在监控”，
/// 也才能解释标题为什么被截断。
/// </summary>
public sealed class IngestPolicy
{
    [JsonPropertyName("privacy_mode")]
    public bool PrivacyMode { get; init; }

    [JsonPropertyName("sample_interval_ms")]
    public int SampleIntervalMs { get; init; }

    [JsonPropertyName("heartbeat_ms")]
    public int HeartbeatMs { get; init; }

    /// <summary>媒体空闲超时（毫秒）。非播放态的媒体会话持续该时长后发送 closed。</summary>
    [JsonPropertyName("media_idle_timeout_ms")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? MediaIdleTimeoutMs { get; init; }

    /// <summary>客户端侧标题长度上限。null 表示不限制。</summary>
    [JsonPropertyName("title_max_chars")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? TitleMaxChars { get; init; }

    [JsonPropertyName("title_truncate_chars")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? TitleTruncateChars { get; init; }
}

public sealed class IngestEvent
{
    /// <summary>不透明唯一标识，同时用作幂等键。</summary>
    [JsonPropertyName("id")]
    public string Id { get; init; } = string.Empty;

    /// <summary>会话内单调递增，用于检测丢包。</summary>
    [JsonPropertyName("sequence")]
    public long Sequence { get; init; }

    [JsonPropertyName("type")]
    public string Type { get; init; } = string.Empty;

    [JsonPropertyName("occurred_at")]
    public DateTime OccurredAt { get; init; }

    /// <summary>本事件为什么被发送。</summary>
    [JsonPropertyName("trigger")]
    public string Trigger { get; init; } = string.Empty;

    /// <summary>类型化负载，形状由 <see cref="Type"/> 决定。</summary>
    [JsonPropertyName("data")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public object? Data { get; init; }
}

public sealed class WindowActivityData
{
    [JsonPropertyName("title")]
    public string Title { get; init; } = string.Empty;

    [JsonPropertyName("app")]
    public string App { get; init; } = string.Empty;

    [JsonPropertyName("process_id")]
    public int ProcessId { get; init; }
}

public sealed class MediaPlaybackData
{
    [JsonPropertyName("title")]
    public string Title { get; init; } = string.Empty;

    [JsonPropertyName("artist")]
    public string Artist { get; init; } = string.Empty;

    [JsonPropertyName("album")]
    public string Album { get; init; } = string.Empty;

    [JsonPropertyName("status")]
    public string Status { get; init; } = string.Empty;

    [JsonPropertyName("source")]
    public string Source { get; init; } = string.Empty;
}

#endregion

#region 响应

public sealed class IngestResponse
{
    [JsonPropertyName("protocol_version")]
    public int ProtocolVersion { get; init; }

    [JsonPropertyName("server_time")]
    public DateTime? ServerTime { get; init; }

    [JsonPropertyName("accepted")]
    public IReadOnlyList<string>? Accepted { get; init; }

    [JsonPropertyName("rejected")]
    public IReadOnlyList<IngestEventRejection>? Rejected { get; init; }

    [JsonPropertyName("ignored")]
    public IReadOnlyList<IngestIgnoredEvent>? Ignored { get; init; }

    [JsonPropertyName("pacing")]
    public IngestPacing? Pacing { get; init; }
}

public sealed class IngestEventRejection
{
    [JsonPropertyName("id")]
    public string Id { get; init; } = string.Empty;

    [JsonPropertyName("code")]
    public string Code { get; init; } = string.Empty;

    [JsonPropertyName("message")]
    public string? Message { get; init; }

    [JsonPropertyName("retryable")]
    public bool Retryable { get; init; }

    /// <summary>机器可读的补充信息，例如 FIELD_TOO_LONG 的 limit / length。</summary>
    [JsonPropertyName("details")]
    public Dictionary<string, JsonElement>? Details { get; init; }

    /// <summary>读取 <c>details.limit</c>，可选地要求 <c>details.field</c> 匹配。</summary>
    public int? GetDetailInt(string key, string? requiredField = null)
    {
        if (Details is null)
        {
            return null;
        }

        if (requiredField is not null &&
            Details.TryGetValue("field", out var field) &&
            field.ValueKind == JsonValueKind.String &&
            !string.Equals(field.GetString(), requiredField, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return Details.TryGetValue(key, out var value) &&
               value.ValueKind == JsonValueKind.Number &&
               value.TryGetInt32(out var parsed)
            ? parsed
            : null;
    }
}

public sealed class IngestIgnoredEvent
{
    [JsonPropertyName("id")]
    public string? Id { get; init; }

    [JsonPropertyName("type")]
    public string Type { get; init; } = string.Empty;

    [JsonPropertyName("code")]
    public string Code { get; init; } = string.Empty;
}

public sealed class IngestPacing
{
    [JsonPropertyName("min_interval_ms")]
    public int? MinIntervalMs { get; init; }

    /// <summary>服务端要求的距下次上报的最小等待时间。</summary>
    [JsonPropertyName("next_upload_after_ms")]
    public int? NextUploadAfterMs { get; init; }
}

public sealed class IngestProblem
{
    [JsonPropertyName("type")]
    public string? Type { get; init; }

    [JsonPropertyName("title")]
    public string? Title { get; init; }

    [JsonPropertyName("status")]
    public int? Status { get; init; }

    [JsonPropertyName("code")]
    public string? Code { get; init; }

    [JsonPropertyName("retryable")]
    public bool? Retryable { get; init; }

    [JsonPropertyName("retry_after_ms")]
    public int? RetryAfterMs { get; init; }

    [JsonPropertyName("detail")]
    public string? Detail { get; init; }
}

public sealed record IngestSendResult(
    int ProtocolVersion,
    IReadOnlyList<string> Accepted,
    IReadOnlyList<IngestEventRejection> Rejected,
    IReadOnlyList<IngestIgnoredEvent> Ignored,
    IngestPacing? Pacing)
{
    public static IngestSendResult Empty { get; } = new(IngestProtocol.Version, [], [], [], null);

    /// <summary>服务端是否回传了逐事件结果。没有则视为整批接受。</summary>
    public bool HasEventResults => Accepted.Count > 0 || Rejected.Count > 0 || Ignored.Count > 0;

    public bool IsAccepted(string eventId) => !HasEventResults || Accepted.Contains(eventId);

    /// <summary>
    /// 幂等命中。按协议 §6.2，DUPLICATE 表示服务端已经处理过该事件，"不算错误"。
    /// </summary>
    public bool IsDuplicate(string eventId) =>
        Rejected.Any(rejection =>
            string.Equals(rejection.Id, eventId, StringComparison.Ordinal) &&
            string.Equals(rejection.Code, IngestProtocol.ErrorCodes.Duplicate, StringComparison.Ordinal));

    /// <summary>
    /// 服务端已经持有该事件的内容。只有为 true 时客户端才更新本地基线，否则下一轮重发。
    /// </summary>
    public bool IsDelivered(string eventId) => IsAccepted(eventId) || IsDuplicate(eventId);
}

#endregion

/// <summary>
/// 请求级失败：HTTP 状态码非 2xx，或响应体不可解析。
/// 事件级的部分失败不走这里，而是通过 <see cref="IngestSendResult"/> 返回。
/// </summary>
public sealed class IngestRequestException : Exception
{
    private static readonly JsonSerializerOptions ProblemOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public IngestRequestException(
        string message,
        int statusCode,
        string code,
        bool retryable,
        int? retryAfterMs,
        string rawBody)
        : base(message)
    {
        StatusCode = statusCode;
        Code = code;
        Retryable = retryable;
        RetryAfterMs = retryAfterMs;
        RawBody = rawBody;
    }

    public int StatusCode { get; }
    public string Code { get; }
    public bool Retryable { get; }
    public int? RetryAfterMs { get; }
    public string RawBody { get; }

    public static IngestRequestException FromResponse(int statusCode, string body)
    {
        var problem = TryParseProblem(body);
        var code = problem?.Code;
        if (string.IsNullOrWhiteSpace(code))
        {
            code = statusCode switch
            {
                401 => IngestProtocol.ErrorCodes.Unauthorized,
                403 => IngestProtocol.ErrorCodes.Forbidden,
                413 => IngestProtocol.ErrorCodes.PayloadTooLarge,
                429 => IngestProtocol.ErrorCodes.RateLimited,
                >= 500 => IngestProtocol.ErrorCodes.ServerError,
                _ => IngestProtocol.ErrorCodes.MalformedRequest
            };
        }

        var retryable = problem?.Retryable ?? statusCode is 429 or >= 500;
        var detail = problem?.Detail ?? problem?.Title;
        var message = string.IsNullOrWhiteSpace(detail)
            ? $"ingest failed: {statusCode} {code}"
            : $"ingest failed: {statusCode} {code} - {detail}";

        return new IngestRequestException(
            message,
            statusCode,
            code,
            retryable,
            problem?.RetryAfterMs,
            body);
    }

    private static IngestProblem? TryParseProblem(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<IngestProblem>(body.Trim(), ProblemOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
