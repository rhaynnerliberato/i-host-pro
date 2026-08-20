using System.Text.Json;
using IHostPro.Contexts.ExternalIntegrations.Application;
using IHostPro.Contexts.ExternalIntegrations.Application.WhatsAppTenantRoutes;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace IHostPro.Contexts.ExternalIntegrations.Api.Controllers;

/// <summary>
/// Meta WhatsApp webhook ingress (Fase 9, Checkpoint 2.3.1/2.3.2/2.3.3 —
/// ADR-022). Verifies the caller is really Meta (GET verify-token handshake;
/// POST <c>X-Hub-Signature-256</c> HMAC over the exact raw bytes), resolves
/// each status entry's tenant and normalizes its status via
/// <see cref="IWhatsAppWebhookStatusProcessor"/>, then durably publishes each
/// <c>Accepted</c> outcome via <see cref="IWhatsAppWebhookStatusEventPublisher"/>
/// (Checkpoint 2.3.3, ADR-022 item 13) before returning 2xx. Still never
/// touches <c>Communication.Message</c> directly — that lifecycle change
/// happens exclusively in Communication's own Wolverine consumer, reacting
/// to the published event, never a synchronous call from here.
///
/// Never uses human JWT auth (<see cref="AllowAnonymousAttribute"/> — Meta is
/// the caller, not a platform user) and never touches any tenant-owned data
/// before a route resolves: credentials come exclusively from
/// <see cref="IWhatsAppWebhookCredentialProvider"/> (app/deployment-level,
/// ADR-022 item 8/9), never <see cref="IWhatsAppCredentialProvider"/>/
/// <c>WhatsAppIntegration</c>.
///
/// Never logs the App Secret, Verify Token, the full signature, the raw
/// request body, the PhoneNumberId, or any recipient/message
/// content/ProviderMessageId value — only sanitized, PII-free structured
/// audit fields (mirrors <c>AuditConfigureWhatsAppIntegrationBehavior</c>'s
/// established convention). TenantId enters structured audit only AFTER a
/// route resolves — never before (mandate §31).
/// </summary>
[ApiController]
[Route("api/v1/integrations/whatsapp/webhook")]
[AllowAnonymous]
public sealed class WhatsAppWebhookController : ControllerBase
{
    private const string VerificationSucceeded = "WhatsAppWebhookVerificationSucceeded";
    private const string VerificationFailed = "WhatsAppWebhookVerificationFailed";
    private const string SignatureAccepted = "WhatsAppWebhookSignatureAccepted";
    private const string SignatureRejected = "WhatsAppWebhookSignatureRejected";
    private const string MalformedPayload = "WhatsAppWebhookMalformedPayload";
    private const string RouteUnknown = "WhatsAppWebhookRouteUnknown";
    private const string StatusNormalized = "WhatsAppWebhookStatusNormalized";
    private const string StatusIgnored = "WhatsAppWebhookStatusIgnored";
    private const string StatusEventPublished = "WhatsAppWebhookStatusEventPublished";

    private readonly IWhatsAppWebhookCredentialProvider _credentialProvider;
    private readonly IWebhookSignatureVerifier _signatureVerifier;
    private readonly IWhatsAppWebhookStatusProcessor _statusProcessor;
    private readonly IWhatsAppWebhookStatusEventPublisher _eventPublisher;
    private readonly ILogger<WhatsAppWebhookController> _logger;

    public WhatsAppWebhookController(
        IWhatsAppWebhookCredentialProvider credentialProvider,
        IWebhookSignatureVerifier signatureVerifier,
        IWhatsAppWebhookStatusProcessor statusProcessor,
        IWhatsAppWebhookStatusEventPublisher eventPublisher,
        ILogger<WhatsAppWebhookController> logger)
    {
        _credentialProvider = credentialProvider;
        _signatureVerifier = signatureVerifier;
        _statusProcessor = statusProcessor;
        _eventPublisher = eventPublisher;
        _logger = logger;
    }

    /// <summary>
    /// Official Meta GET verification contract: <c>hub.mode</c>/
    /// <c>hub.verify_token</c>/<c>hub.challenge</c>. On success, the response
    /// body is the raw <c>hub.challenge</c> value — plain text, never JSON.
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> Verify(
        [FromQuery(Name = "hub.mode")] string? mode,
        [FromQuery(Name = "hub.verify_token")] string? verifyToken,
        [FromQuery(Name = "hub.challenge")] string? challenge,
        CancellationToken cancellationToken)
    {
        var configuredVerifyToken = await _credentialProvider.GetVerifyTokenAsync(cancellationToken);

        if (string.IsNullOrEmpty(configuredVerifyToken) ||
            !_signatureVerifier.IsValidVerifyToken(mode, verifyToken, configuredVerifyToken))
        {
            _logger.LogWarning("{AuditEvent}: mode {Mode}", VerificationFailed, mode);
            // A plain 403, not Forbid() — this endpoint must never depend on
            // an authentication scheme being registered (ADR-022, item 3:
            // no human JWT auth involved here at all).
            return StatusCode(StatusCodes.Status403Forbidden);
        }

        _logger.LogInformation("{AuditEvent}: mode {Mode}", VerificationSucceeded, mode);
        return Content(challenge ?? string.Empty, "text/plain");
    }

    /// <summary>
    /// Reads the exact raw request body bytes (no <c>[FromBody]</c> model
    /// binding — the signature must be computed over precisely what was
    /// received, before any deserialization) and verifies
    /// <c>X-Hub-Signature-256</c>. Only after a valid signature does this
    /// touch <see cref="IWhatsAppWebhookStatusProcessor"/> — never before
    /// (mandate §14/§26: zero tenant-owned lookup before signature).
    /// </summary>
    [HttpPost]
    public async Task<IActionResult> Receive(CancellationToken cancellationToken)
    {
        using var bodyStream = new MemoryStream();
        await Request.Body.CopyToAsync(bodyStream, cancellationToken);
        var rawBody = bodyStream.ToArray();

        var configuredAppSecret = await _credentialProvider.GetAppSecretAsync(cancellationToken);
        var signatureHeader = Request.Headers["X-Hub-Signature-256"].ToString();

        if (string.IsNullOrEmpty(configuredAppSecret) ||
            !_signatureVerifier.IsValidSignature(rawBody, signatureHeader, configuredAppSecret))
        {
            _logger.LogWarning("{AuditEvent}: bodyLength {BodyLength}", SignatureRejected, rawBody.Length);
            return Unauthorized();
        }

        // Signed-but-permanently-malformed payloads still return 2xx — Meta
        // retries a non-200 response for up to 7 days, and a malformed body
        // will never become processable on retry (approved response policy,
        // Checkpoint 2.3.0 decision D).
        if (!IsWellFormedJson(rawBody))
        {
            _logger.LogWarning("{AuditEvent}: bodyLength {BodyLength}", MalformedPayload, rawBody.Length);
            return Ok();
        }

        _logger.LogInformation("{AuditEvent}: bodyLength {BodyLength}", SignatureAccepted, rawBody.Length);

        var outcomes = await _statusProcessor.ProcessAsync(rawBody, cancellationToken);
        foreach (var outcome in outcomes)
        {
            LogOutcome(outcome);

            // Fase 9, Checkpoint 2.3.3 (ADR-022 item 13/mandate §10/§11): a
            // durable-outbox failure here must propagate uncaught — the
            // default ASP.NET Core unhandled-exception response is 5xx,
            // which is exactly what makes Meta retry this delivery. Never
            // caught/swallowed into the 2xx path below.
            if (outcome.Kind == WebhookStatusOutcomeKind.Accepted)
            {
                await _eventPublisher.PublishAsync(outcome, cancellationToken);
                _logger.LogInformation("{AuditEvent}: tenant {TenantId}", StatusEventPublished, outcome.TenantId);
            }
        }

        // Every outcome kind returns 2xx here — Accepted/UnknownRoute/Malformed
        // are all permanent-for-this-payload classifications, never a reason
        // to make Meta retry (Checkpoint 2.3.0 decision D). An Accepted
        // outcome only reaches this line once its event is already durably
        // published (see the throw-propagates-to-5xx note above).
        return Ok();
    }

    private void LogOutcome(WebhookStatusProcessingOutcome outcome)
    {
        switch (outcome.Kind)
        {
            case WebhookStatusOutcomeKind.Accepted:
                _logger.LogInformation(
                    "{AuditEvent}: tenant {TenantId} status {Status} hasErrorCode {HasErrorCode}",
                    StatusNormalized, outcome.TenantId, outcome.NormalizedStatus, outcome.ProviderErrorCode is not null);
                break;
            case WebhookStatusOutcomeKind.UnknownRoute:
                _logger.LogWarning("{AuditEvent}", RouteUnknown);
                break;
            case WebhookStatusOutcomeKind.Malformed:
                _logger.LogWarning(
                    "{AuditEvent}: tenant {TenantId}", StatusIgnored, outcome.TenantId);
                break;
        }
    }

    private static bool IsWellFormedJson(byte[] rawBody)
    {
        try
        {
            using var _ = JsonDocument.Parse(rawBody);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
