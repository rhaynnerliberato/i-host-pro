namespace IHostPro.Contexts.ExternalIntegrations.Application;

/// <summary>
/// Fase 12, Checkpoint 3 (Resilience &amp; Rate Limiting) — the "Webhook"
/// HTTP category (Decision Gate §2). A thin abstraction, mirroring
/// <see cref="IWhatsAppWebhookCredentialProvider"/>/<see cref="IWebhookSignatureVerifier"/>'s
/// own shape exactly, so <c>WhatsAppWebhookController</c> (Api project — never
/// allowed to reference Infrastructure) can apply the shared Redis-backed
/// rate limiter without depending on it directly. The real implementation
/// (<c>WebhookRateLimiter</c>, Infrastructure) delegates to
/// <c>IDistributedRateLimiter</c> — never a second limiting algorithm.
/// </summary>
public interface IWebhookRateLimiter
{
    /// <summary><paramref name="partitionKey"/> must be a provider/account-level technical identifier (e.g. the WhatsApp phone_number_id) — never the guest's phone number, message body, or any other PII.</summary>
    Task<WebhookRateLimitDecision> CheckAsync(string partitionKey, CancellationToken cancellationToken);
}

public readonly record struct WebhookRateLimitDecision(bool Allowed, TimeSpan? RetryAfter);
