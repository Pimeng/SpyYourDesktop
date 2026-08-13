using System.Reflection;
using System.Text;
using System.Text.Json;
using Desktop.Infrastructure;
using Desktop.Models;

namespace Desktop.Services;

public interface IUsageIngestService
{
    Task SendAsync(UploadEvent payload, MonitorSettings settings, CancellationToken cancellationToken);
}

public sealed class UsageIngestService(HttpClient httpClient) : IUsageIngestService
{
    public async Task SendAsync(UploadEvent payload, MonitorSettings settings, CancellationToken cancellationToken)
    {
        payload.AppVersion = GetCurrentVersion();
        payload.Os = "Windows";

        using var request = new HttpRequestMessage(HttpMethod.Post, settings.ServerUrl);
        request.Content = new StringContent(
            JsonSerializer.Serialize(payload),
            Encoding.UTF8,
            "application/json");
        request.Headers.TryAddWithoutValidation("X-App-Version", payload.AppVersion);
        request.Headers.TryAddWithoutValidation("X-OS", payload.Os);

        var key = settings.UploadKey.Trim();
        if (key.Length > 0)
        {
            request.Headers.TryAddWithoutValidation("x-name-key", key);
            request.Headers.TryAddWithoutValidation(
                "Authorization",
                key.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? key : $"Bearer {key}");
        }

        using var response = await httpClient.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        var (serverError, serverMessage) = ServerErrorParser.TryExtract(body);
        var numericOnly = ServerErrorParser.IsAllDigits(body.Trim());

        if (!response.IsSuccessStatusCode || serverError is not null || numericOnly)
        {
            var statusCode = (int)response.StatusCode;
            if (statusCode == 0 && numericOnly && int.TryParse(body.Trim(), out var numericCode))
            {
                statusCode = numericCode;
            }

            var message = serverError is not null && serverMessage is not null
                ? $"ingest failed: {statusCode} {serverError} - {serverMessage}"
                : serverError is not null
                    ? $"ingest failed: {statusCode} {serverError}"
                    : $"ingest failed: {statusCode} {body}";

            throw new IngestErrorException(
                message,
                statusCode,
                serverError ?? (numericOnly ? $"code {body.Trim()}" : null),
                serverMessage ?? body);
        }
    }

    private static string GetCurrentVersion()
    {
        var version = Assembly.GetEntryAssembly()?
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion;
        return string.IsNullOrWhiteSpace(version) ? "1.0.0.0" : version;
    }
}

internal static class ServerErrorParser
{
    public static (string? Error, string? Message) TryExtract(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return (null, null);
        }

        var text = body.Trim();
        var jsonStart = text.IndexOf('{');
        if (jsonStart >= 0)
        {
            try
            {
                using var document = JsonDocument.Parse(text[jsonStart..]);
                var root = document.RootElement;
                if (root.ValueKind == JsonValueKind.Object)
                {
                    var error = root.TryGetProperty("error", out var errorElement) &&
                                errorElement.ValueKind == JsonValueKind.String
                        ? errorElement.GetString()
                        : null;
                    var message = root.TryGetProperty("message", out var messageElement) &&
                                  messageElement.ValueKind == JsonValueKind.String
                        ? messageElement.GetString()
                        : null;
                    if (!string.IsNullOrWhiteSpace(error) || !string.IsNullOrWhiteSpace(message))
                    {
                        return (error, message);
                    }
                }
            }
            catch (JsonException)
            {
            }
        }

        return text.Contains("error", StringComparison.OrdinalIgnoreCase)
            ? ("Error", text)
            : (null, null);
    }

    public static int ParseRetryAfterMilliseconds(string text, int fallbackMilliseconds = 800)
    {
        try
        {
            using var document = JsonDocument.Parse(text.Trim());
            var root = document.RootElement;
            if (root.TryGetProperty("retry_after_ms", out var retry) && retry.TryGetInt32(out var retryMilliseconds) && retryMilliseconds > 0)
            {
                return retryMilliseconds;
            }

            int? minimum = null;
            int? elapsed = null;
            if (root.TryGetProperty("min_interval_ms", out var minimumElement) && minimumElement.TryGetInt32(out var minimumValue))
            {
                minimum = minimumValue;
            }

            if (root.TryGetProperty("elapsed_ms", out var elapsedElement) && elapsedElement.TryGetInt32(out var elapsedValue))
            {
                elapsed = elapsedValue;
            }

            if (minimum.HasValue && elapsed.HasValue)
            {
                return Math.Max(0, minimum.Value - elapsed.Value);
            }
        }
        catch (JsonException)
        {
        }

        return fallbackMilliseconds;
    }

    public static bool IsWindowTitleTooLong(IngestErrorException exception)
    {
        var text = exception.ServerError ?? exception.RawBody;
        return text.Contains("window title too long", StringComparison.OrdinalIgnoreCase)
            || text.Contains("window_title too long", StringComparison.OrdinalIgnoreCase);
    }

    public static (int? Limit, int? Length) ExtractLimitLength(string text)
    {
        int? limit = null;
        int? length = null;
        try
        {
            using var document = JsonDocument.Parse(NormalizeToJson(text));
            var root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Object)
            {
                if (root.TryGetProperty("limit", out var limitElement) && limitElement.TryGetInt32(out var limitValue))
                {
                    limit = limitValue;
                }

                if (root.TryGetProperty("length", out var lengthElement) && lengthElement.TryGetInt32(out var lengthValue))
                {
                    length = lengthValue;
                }

                if (limit.HasValue || length.HasValue)
                {
                    return (limit, length);
                }
            }
        }
        catch (JsonException)
        {
        }

        var limitMatch = System.Text.RegularExpressions.Regex.Match(text, @"\blimit\s*[:=]\s*(\d+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (limitMatch.Success && int.TryParse(limitMatch.Groups[1].Value, out var parsedLimit))
        {
            limit = parsedLimit;
        }

        var lengthMatch = System.Text.RegularExpressions.Regex.Match(text, @"\blength\s*[:=]\s*(\d+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (lengthMatch.Success && int.TryParse(lengthMatch.Groups[1].Value, out var parsedLength))
        {
            length = parsedLength;
        }

        return (limit, length);
    }

    private static string NormalizeToJson(string value)
    {
        var text = value.Trim();
        return text.StartsWith('(') && text.EndsWith(')')
            ? "{" + text[1..^1] + "}"
            : text;
    }

    public static bool IsAllDigits(string? value) =>
        !string.IsNullOrEmpty(value) && value.All(char.IsDigit);
}
