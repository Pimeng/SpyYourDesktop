using System.Reflection;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Desktop.Models;

namespace Desktop.Services;

public interface IUsageIngestService
{
    /// <summary>
    /// 按协议 v2 组装并发送一批事件。
    /// 请求级失败抛出 <see cref="IngestRequestException"/>；事件级部分失败由返回值反馈。
    /// </summary>
    Task<IngestSendResult> SendAsync(
        MonitorSettings settings,
        IngestSessionInfo session,
        IReadOnlyList<IngestEvent> events,
        CancellationToken cancellationToken);
}

public sealed class UsageIngestService(HttpClient httpClient) : IUsageIngestService
{
    private static readonly JsonSerializerOptions RequestOptions = new()
    {
        // 中文标题/艺术家直接以 UTF-8 输出，而不是 \uXXXX 转义，避免体积翻倍。
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private static readonly JsonSerializerOptions ResponseOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public async Task<IngestSendResult> SendAsync(
        MonitorSettings settings,
        IngestSessionInfo session,
        IReadOnlyList<IngestEvent> events,
        CancellationToken cancellationToken)
    {
        var request = BuildRequest(settings, session, events);

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, settings.ServerUrl)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(request, RequestOptions),
                Encoding.UTF8,
                "application/json")
        };
        httpRequest.Headers.TryAddWithoutValidation("X-Protocol-Version", IngestProtocol.Version.ToString());
        httpRequest.Headers.TryAddWithoutValidation("X-App-Version", request.Client.Version);

        var key = settings.UploadKey.Trim();
        if (key.Length > 0)
        {
            httpRequest.Headers.TryAddWithoutValidation(
                "Authorization",
                key.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? key : $"Bearer {key}");
        }

        using var response = await httpClient.SendAsync(httpRequest, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        var statusCode = (int)response.StatusCode;

        if (!response.IsSuccessStatusCode)
        {
            throw IngestRequestException.FromResponse(statusCode, body);
        }

        if (string.IsNullOrWhiteSpace(body))
        {
            // 204 或空体：整批接受，服务端没有逐事件反馈。
            return IngestSendResult.Empty;
        }

        IngestResponse? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<IngestResponse>(body, ResponseOptions);
        }
        catch (JsonException exception)
        {
            throw new IngestRequestException(
                $"ingest returned {statusCode} but the body is not valid JSON: {exception.Message}",
                statusCode,
                IngestProtocol.ErrorCodes.MalformedRequest,
                retryable: false,
                retryAfterMs: null,
                rawBody: body);
        }

        if (parsed is null)
        {
            return IngestSendResult.Empty;
        }

        return new IngestSendResult(
            parsed.ProtocolVersion,
            parsed.Accepted ?? [],
            parsed.Rejected ?? [],
            parsed.Ignored ?? [],
            parsed.Pacing);
    }

    private static IngestRequest BuildRequest(
        MonitorSettings settings,
        IngestSessionInfo session,
        IReadOnlyList<IngestEvent> events)
    {
        var capabilities = new List<string> { IngestProtocol.EventTypes.WindowActivity };
        if (settings.ReportMedia)
        {
            capabilities.Add(IngestProtocol.EventTypes.MediaPlayback);
        }

        return new IngestRequest
        {
            Client = new IngestClientInfo
            {
                Name = IngestProtocol.ClientName,
                Version = GetCurrentVersion(),
                Platform = "windows",
                OsVersion = GetOsVersion(),
                Capabilities = capabilities
            },
            Device = new IngestDeviceInfo { Id = settings.MachineId },
            Session = session,
            SentAt = ProtocolTime.UtcNow(),
            Policy = new IngestPolicy
            {
                PrivacyMode = settings.PrivacyMode,
                SampleIntervalMs = Math.Clamp(settings.IntervalSeconds, 5, 3600) * 1000,
                HeartbeatMs = Math.Clamp(settings.HeartbeatSeconds, 10, 3600) * 1000,
                TitleMaxChars = settings.ForceAllowLongTitle ? null : 150,
                TitleTruncateChars = settings.ForceAllowLongTitle ? null : 140
            },
            Events = events
        };
    }

    private static string GetCurrentVersion()
    {
        var version = Assembly.GetEntryAssembly()?
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion;
        return string.IsNullOrWhiteSpace(version) ? "1.0.0.0" : version;
    }

    private static string GetOsVersion()
    {
        try
        {
            return Environment.OSVersion.Version.ToString();
        }
        catch
        {
            return string.Empty;
        }
    }
}

