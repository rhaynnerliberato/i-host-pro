using System.Text.Json;
using IHostPro.Contexts.ExternalIntegrations.Application;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace IHostPro.Contexts.ExternalIntegrations.Api.Controllers;

/// <summary>
/// Meta WhatsApp webhook security ingress (Fase 9, Checkpoint 2.3.1 —
/// ADR-022). Deliberately foundation-only: verifies the caller is really
/// Meta (GET verify-token handshake; POST <c>X-Hub-Signature-256</c> HMAC
/// over the exact raw bytes) and does nothing else — no tenant resolution,
/// no status normalization, no <c>Message</c> lifecycle change, no
/// Integration Event. Those belong to CP2.3.2/2.3.3 (ADR-022, items 10/11/13).
///
/// Never uses human JWT auth (<see cref="AllowAnonymousAttribute"/> — Meta is
/// the caller, not a platform user) and never touches any tenant-owned data:
/// credentials come exclusively from <see cref="IWhatsAppWebhookCredentialProvider"/>
/// (app/deployment-level, ADR-022 item 8/9), never
/// <see cref="IWhatsAppCredentialProvider"/>/<c>WhatsAppIntegration</c>.
///
/// Never logs the App Secret, Verify Token, the full signature, the raw
/// request body, or any recipient/message content — only sanitized,
/// PII-free structured audit fields (mirrors
/// <c>AuditConfigureWhatsAppIntegrationBehavior</c>'s established convention).
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

    private readonly IWhatsAppWebhookCredentialProvider _credentialProvider;
    private readonly IWebhookSignatureVerifier _signatureVerifier;
    private readonly ILogger<WhatsAppWebhookController> _logger;

    public WhatsAppWebhookController(
        IWhatsAppWebhookCredentialProvider credentialProvider,
        IWebhookSignatureVerifier signatureVerifier,
        ILogger<WhatsAppWebhookController> logger)
    {
        _credentialProvider = credentialProvider;
        _signatureVerifier = signatureVerifier;
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
    /// <c>X-Hub-Signature-256</c>. CP2.3.1 stops at "signature valid,
    /// well-formed JSON acknowledgment" — no status parsing, no tenant
    /// lookup, no persistence (ADR-022, item 11/13 — those are later
    /// checkpoints).
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
        return Ok();
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
