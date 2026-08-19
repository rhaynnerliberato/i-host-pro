using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using IHostPro.Contexts.ExternalIntegrations.Application;
using IHostPro.Contexts.ExternalIntegrations.Application.WhatsAppIntegrations;
using IHostPro.Contexts.ExternalIntegrations.Application.WhatsAppTemplateMappings;
using IHostPro.Contexts.ExternalIntegrations.Contracts;
using IHostPro.Contexts.ExternalIntegrations.Domain;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace IHostPro.Contexts.ExternalIntegrations.Infrastructure.Meta;

/// <summary>
/// Real <see cref="IMessagingProvider"/> implementation for the Meta
/// WhatsApp Cloud API (Fase 9, Checkpoint 2.2 — mandate §6). Meta-specific
/// types/vocabulary live ONLY here, never in <c>Contracts</c> (ADR-021).
///
/// Resolves credentials exclusively via <see cref="IWhatsAppCredentialProvider"/>
/// (mandate §14 — never <c>IConfiguration</c> directly) and the template
/// mapping exclusively via <see cref="IWhatsAppTemplateMappingRepository"/>
/// (mandate §22/§23 — never a caller-supplied Meta template name). Uses
/// <see cref="IHttpClientFactory"/> (mandate §11 — never <c>new HttpClient()</c>),
/// makes exactly one HTTP call, and never retries automatically (mandate
/// §12/§13: a timeout or network interruption maps to
/// <see cref="ProviderFailureCategory.DeliveryOutcomeUnknown"/>, never a
/// second attempt).
///
/// Never logs <c>Destination</c>/template parameter values/the access token/
/// the raw provider response body (mandate §24/§34/§35).
/// </summary>
public sealed class MetaWhatsAppMessagingProvider : IMessagingProvider
{
    /// <summary>Named <see cref="IHttpClientFactory"/> client — see <c>ExternalIntegrationsModuleExtensions</c> for its registration (base address, timeout).</summary>
    public const string HttpClientName = "ExternalIntegrations.Meta.WhatsApp";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IWhatsAppIntegrationRepository _integrationRepository;
    private readonly IWhatsAppTemplateMappingRepository _templateMappingRepository;
    private readonly IWhatsAppCredentialProvider _credentialProvider;
    private readonly IOptions<MetaWhatsAppOptions> _options;
    private readonly ILogger<MetaWhatsAppMessagingProvider> _logger;

    public MetaWhatsAppMessagingProvider(
        IHttpClientFactory httpClientFactory,
        IWhatsAppIntegrationRepository integrationRepository,
        IWhatsAppTemplateMappingRepository templateMappingRepository,
        IWhatsAppCredentialProvider credentialProvider,
        IOptions<MetaWhatsAppOptions> options,
        ILogger<MetaWhatsAppMessagingProvider> logger)
    {
        _httpClientFactory = httpClientFactory;
        _integrationRepository = integrationRepository;
        _templateMappingRepository = templateMappingRepository;
        _credentialProvider = credentialProvider;
        _options = options;
        _logger = logger;
    }

    public async Task<OutboundMessageResult> SendAsync(OutboundMessageRequest request, CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();

        var integration = await _integrationRepository.GetForCurrentTenantAsync(cancellationToken);
        if (string.IsNullOrEmpty(integration?.PhoneNumberId))
            return Reject(request, stopwatch, "integration_not_configured", ProviderFailureCategory.PermanentFailure);

        if (string.IsNullOrEmpty(integration.AccessTokenSecretReference))
            return Reject(request, stopwatch, "access_token_not_configured", ProviderFailureCategory.AuthenticationFailed);

        var accessToken = await _credentialProvider.GetSecretAsync(integration.AccessTokenSecretReference, cancellationToken);
        if (string.IsNullOrEmpty(accessToken))
            return Reject(request, stopwatch, "access_token_not_resolved", ProviderFailureCategory.AuthenticationFailed);

        var mapping = await _templateMappingRepository.GetForCurrentTenantByTemplateKeyAsync(request.TemplateKey, cancellationToken);
        if (mapping is null)
            return Reject(request, stopwatch, "template_mapping_not_configured", ProviderFailureCategory.InvalidTemplate);

        var requestBody = BuildRequestBody(request, mapping);

        using var httpRequest = new HttpRequestMessage(
            HttpMethod.Post, $"{_options.Value.GraphApiVersion}/{integration.PhoneNumberId}/messages")
        {
            Content = JsonContent.Create(requestBody),
        };
        httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        var httpClient = _httpClientFactory.CreateClient(HttpClientName);

        HttpResponseMessage response;
        string responseBody;
        try
        {
            response = await httpClient.SendAsync(httpRequest, cancellationToken);
            responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // HTTP client timeout — the request may or may not have reached
            // Meta; never treated as a rejection, never retried (mandate §12/§30).
            return OutcomeUnknown(request, stopwatch, "timeout");
        }
        catch (HttpRequestException)
        {
            return OutcomeUnknown(request, stopwatch, "network_error");
        }

        if (response.IsSuccessStatusCode)
        {
            var providerMessageId = TryParseProviderMessageId(responseBody);
            if (providerMessageId is null)
                return OutcomeUnknown(request, stopwatch, "malformed_success_response");

            return Accept(request, stopwatch, providerMessageId);
        }

        var errorCode = TryParseErrorCode(responseBody);
        var category = MetaFailureCodes.MapToCategory(response.StatusCode, errorCode);
        var failureCode = errorCode?.ToString() ?? $"http_{(int)response.StatusCode}";

        return Reject(request, stopwatch, failureCode, category);
    }

    private static object BuildRequestBody(OutboundMessageRequest request, WhatsAppTemplateMapping mapping)
    {
        var parameters = mapping.ParameterOrder
            .Select(variableName => new MetaTemplateParameter
            {
                Text = request.TemplateVariables.TryGetValue(variableName, out var value) ? value : string.Empty,
            })
            .ToList();

        return new MetaSendTemplateMessageRequest
        {
            To = request.Destination,
            Template = new MetaTemplate
            {
                Name = mapping.ProviderTemplateName,
                Language = new MetaTemplateLanguage { Code = mapping.LanguageCode },
                Components = parameters.Count == 0
                    ? null
                    : [new MetaTemplateComponent { Type = "body", Parameters = parameters }],
            },
        };
    }

    private static string? TryParseProviderMessageId(string responseBody)
    {
        try
        {
            var parsed = JsonSerializer.Deserialize<MetaSendMessageResponse>(responseBody);
            var id = parsed?.Messages?.FirstOrDefault()?.Id;
            return string.IsNullOrEmpty(id) ? null : id;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static int? TryParseErrorCode(string responseBody)
    {
        try
        {
            return JsonSerializer.Deserialize<MetaErrorResponse>(responseBody)?.Error?.Code;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private OutboundMessageResult Accept(OutboundMessageRequest request, Stopwatch stopwatch, string providerMessageId)
    {
        LogOutcome(request, stopwatch, "Accepted", providerMessageId, null, null);
        return new OutboundMessageResult(true, providerMessageId, null, null);
    }

    private OutboundMessageResult Reject(
        OutboundMessageRequest request, Stopwatch stopwatch, string failureCode, ProviderFailureCategory category)
    {
        LogOutcome(request, stopwatch, "Rejected", null, failureCode, category);
        return new OutboundMessageResult(false, null, failureCode, category);
    }

    private OutboundMessageResult OutcomeUnknown(OutboundMessageRequest request, Stopwatch stopwatch, string failureCode)
    {
        LogOutcome(request, stopwatch, "DeliveryOutcomeUnknown", null, failureCode, ProviderFailureCategory.DeliveryOutcomeUnknown);
        return new OutboundMessageResult(false, null, failureCode, ProviderFailureCategory.DeliveryOutcomeUnknown);
    }

    /// <summary>
    /// Exactly one structured entry per attempt (mandate §34/§35) — never
    /// <c>Destination</c>, never a template parameter value, never the
    /// access token, never the raw provider response body.
    /// </summary>
    private void LogOutcome(
        OutboundMessageRequest request, Stopwatch stopwatch, string result,
        string? providerMessageId, string? failureCode, ProviderFailureCategory? failureCategory)
    {
        var level = result == "Accepted" ? LogLevel.Information : LogLevel.Warning;

        _logger.Log(
            level,
            "Meta WhatsApp dispatch {Result} for tenant {TenantId} message {MessageId} " +
            "(providerMessageId {ProviderMessageId}, failureCode {FailureCode}, failureCategory {FailureCategory}, {DurationMs}ms)",
            result, request.TenantId, request.MessageId, providerMessageId, failureCode, failureCategory, stopwatch.ElapsedMilliseconds);
    }
}
