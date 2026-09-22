using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using IHostPro.Contexts.ExternalIntegrations.Application.AirbnbEmailBridge;
using Microsoft.Extensions.Logging;

namespace IHostPro.Contexts.ExternalIntegrations.Infrastructure.AirbnbEmailBridge;

/// <summary>
/// Microsoft Graph <c>/messages/delta</c>-backed <see cref="IAirbnbEmailMessageSource"/>.
/// Graph-specific vocabulary/types live only here, in Infrastructure — never
/// in Application/Domain/Api (this Bounded Context's own architecture rule;
/// see <c>IAirbnbEmailMessageSource</c>'s own remarks).
///
/// A caller-supplied cursor (<c>deltaOrNextLink</c>) is always used as the
/// FULL, opaque request URI — never parsed, rewritten, or given extra query
/// parameters (Fase 9 review §7); <c>$select</c> is only ever added on a
/// fresh (no-cursor) request, since Graph's delta preserves the original
/// select list through both <c>@odata.nextLink</c> and <c>@odata.deltaLink</c>
/// automatically. One bounded retry on HTTP 429, honoring <c>Retry-After</c>
/// (capped) — a deliberate, explicitly-flagged deviation from this
/// codebase's otherwise universal "never retry an outbound HTTP call"
/// convention (see <c>MetaWhatsAppOptions</c>'s own doc comment): a delta
/// page fetch is naturally idempotent (the cursor never advances until the
/// page is durably processed), so a bounded retry here cannot cause a
/// duplicate side effect the way retrying a WhatsApp send could.
/// </summary>
public sealed class MicrosoftGraphEmailMessageSource : IAirbnbEmailMessageSource
{
    public const string HttpClientName = "ExternalIntegrations.AirbnbEmailBridge.Graph";
    private const string GraphBaseUrl = "https://graph.microsoft.com/v1.0/";
    private const int MaxRetryAttempts = 2;
    private static readonly TimeSpan MaxRetryDelay = TimeSpan.FromSeconds(30);

    private static readonly string[] SelectFields =
        ["id", "internetMessageId", "receivedDateTime", "subject", "from", "sender", "bodyPreview"];

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<MicrosoftGraphEmailMessageSource> _logger;

    public MicrosoftGraphEmailMessageSource(IHttpClientFactory httpClientFactory, ILogger<MicrosoftGraphEmailMessageSource> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public async Task<AirbnbEmailDeltaFetchOutcome> GetDeltaPageAsync(
        string accessToken, string mailFolderId, string? deltaOrNextLink, CancellationToken cancellationToken)
    {
        var requestUri = deltaOrNextLink
            ?? $"{GraphBaseUrl}me/mailFolders/{Uri.EscapeDataString(mailFolderId)}/messages/delta?$select={string.Join(',', SelectFields)}";

        var httpClient = _httpClientFactory.CreateClient(HttpClientName);

        for (var attempt = 0; ; attempt++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

            HttpResponseMessage response;
            try
            {
                response = await httpClient.SendAsync(request, cancellationToken);
            }
            catch (HttpRequestException ex)
            {
                _logger.LogWarning(ex, "Airbnb Email Bridge delta fetch network error.");
                return AirbnbEmailDeltaFetchOutcome.Failure(AirbnbEmailDeltaFetchFailureReason.TransientFailure, "network_error");
            }
            catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return AirbnbEmailDeltaFetchOutcome.Failure(AirbnbEmailDeltaFetchFailureReason.TransientFailure, "timeout");
            }

            if (response.StatusCode == HttpStatusCode.TooManyRequests && attempt < MaxRetryAttempts)
            {
                var delay = response.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(5);
                if (delay > MaxRetryDelay)
                    delay = MaxRetryDelay;

                _logger.LogInformation("Airbnb Email Bridge delta fetch throttled (429) - retrying after {DelaySeconds}s.", delay.TotalSeconds);
                await Task.Delay(delay, cancellationToken);
                continue;
            }

            if (response.StatusCode == HttpStatusCode.Unauthorized)
                return AirbnbEmailDeltaFetchOutcome.Failure(AirbnbEmailDeltaFetchFailureReason.AuthenticationFailed, "http_401");

            if (response.StatusCode is HttpStatusCode.Gone or HttpStatusCode.BadRequest)
            {
                // Graph returns 410 Gone (and sometimes 400) for an expired/invalid deltaLink.
                var body = await response.Content.ReadAsStringAsync(cancellationToken);
                if (IsResyncRequired(body))
                    return AirbnbEmailDeltaFetchOutcome.Failure(AirbnbEmailDeltaFetchFailureReason.InvalidDeltaLink, "delta_resync_required");

                return AirbnbEmailDeltaFetchOutcome.Failure(
                    AirbnbEmailDeltaFetchFailureReason.TransientFailure, $"http_{(int)response.StatusCode}");
            }

            if (!response.IsSuccessStatusCode)
            {
                return AirbnbEmailDeltaFetchOutcome.Failure(
                    AirbnbEmailDeltaFetchFailureReason.TransientFailure, $"http_{(int)response.StatusCode}");
            }

            var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
            return ParseSuccessResponse(responseBody);
        }
    }

    public async Task<AirbnbEmailMessageContent?> GetMessageContentAsync(string accessToken, string messageId, CancellationToken cancellationToken)
    {
        var httpClient = _httpClientFactory.CreateClient(HttpClientName);
        var requestUri = $"{GraphBaseUrl}me/messages/{Uri.EscapeDataString(messageId)}?$select=subject,body";

        using var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        HttpResponseMessage response;
        try
        {
            response = await httpClient.SendAsync(request, cancellationToken);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            _logger.LogWarning(ex, "Airbnb Email Bridge message content fetch failed.");
            return null;
        }

        if (!response.IsSuccessStatusCode)
            return null;

        try
        {
            var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
            var parsed = JsonSerializer.Deserialize<GraphMessageContentResponse>(responseBody);
            return parsed is null ? null : new AirbnbEmailMessageContent(parsed.Subject, parsed.Body?.Content);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Airbnb Email Bridge message content fetch returned malformed JSON.");
            return null;
        }
    }

    private AirbnbEmailDeltaFetchOutcome ParseSuccessResponse(string responseBody)
    {
        GraphDeltaResponse? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<GraphDeltaResponse>(responseBody);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Airbnb Email Bridge delta fetch returned malformed JSON.");
            return AirbnbEmailDeltaFetchOutcome.Failure(AirbnbEmailDeltaFetchFailureReason.TransientFailure, "malformed_response");
        }

        if (parsed is null)
            return AirbnbEmailDeltaFetchOutcome.Failure(AirbnbEmailDeltaFetchFailureReason.TransientFailure, "empty_response");

        var messages = (parsed.Value ?? [])
            .Select(m => new Application.AirbnbEmailBridge.AirbnbEmailMessageSummary(
                m.Id ?? string.Empty,
                m.InternetMessageId,
                m.ReceivedDateTime ?? DateTimeOffset.UtcNow,
                m.Subject,
                m.From?.EmailAddress?.Address,
                m.Sender?.EmailAddress?.Address,
                m.BodyPreview))
            .Where(m => !string.IsNullOrEmpty(m.MessageId))
            .ToList();

        return AirbnbEmailDeltaFetchOutcome.Success(new AirbnbEmailDeltaPage(messages, parsed.NextLink, parsed.DeltaLink));
    }

    private static bool IsResyncRequired(string responseBody)
    {
        // Graph's documented signal for "the delta state is gone, start over":
        // an error code of ResyncRequired, or a top-level SyncStateNotFound.
        return responseBody.Contains("ResyncRequired", StringComparison.OrdinalIgnoreCase)
            || responseBody.Contains("SyncStateNotFound", StringComparison.OrdinalIgnoreCase);
    }

    private sealed class GraphDeltaResponse
    {
        [JsonPropertyName("@odata.nextLink")]
        public string? NextLink { get; set; }

        [JsonPropertyName("@odata.deltaLink")]
        public string? DeltaLink { get; set; }

        [JsonPropertyName("value")]
        public List<GraphMessage>? Value { get; set; }
    }

    private sealed class GraphMessage
    {
        [JsonPropertyName("id")]
        public string? Id { get; set; }

        [JsonPropertyName("internetMessageId")]
        public string? InternetMessageId { get; set; }

        [JsonPropertyName("receivedDateTime")]
        public DateTimeOffset? ReceivedDateTime { get; set; }

        [JsonPropertyName("subject")]
        public string? Subject { get; set; }

        [JsonPropertyName("from")]
        public GraphRecipient? From { get; set; }

        [JsonPropertyName("sender")]
        public GraphRecipient? Sender { get; set; }

        [JsonPropertyName("bodyPreview")]
        public string? BodyPreview { get; set; }
    }

    private sealed class GraphRecipient
    {
        [JsonPropertyName("emailAddress")]
        public GraphEmailAddress? EmailAddress { get; set; }
    }

    private sealed class GraphEmailAddress
    {
        [JsonPropertyName("address")]
        public string? Address { get; set; }
    }

    private sealed class GraphMessageContentResponse
    {
        [JsonPropertyName("subject")]
        public string? Subject { get; set; }

        [JsonPropertyName("body")]
        public GraphItemBody? Body { get; set; }
    }

    private sealed class GraphItemBody
    {
        [JsonPropertyName("content")]
        public string? Content { get; set; }
    }
}
