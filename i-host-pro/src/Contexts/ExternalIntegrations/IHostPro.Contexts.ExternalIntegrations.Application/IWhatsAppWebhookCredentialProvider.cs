namespace IHostPro.Contexts.ExternalIntegrations.Application;

/// <summary>
/// Resolves the App Secret and Verify Token needed to secure the Meta
/// WhatsApp webhook (Fase 9, Checkpoint 2.3.1 — ADR-022, item 8/9).
/// Deliberately separate from <see cref="IWhatsAppCredentialProvider"/>:
/// that one resolves a tenant's own opaque secret reference (via
/// <c>WhatsAppIntegration</c>, requires a resolved <c>TenantId</c>); this one
/// resolves app/deployment-level credentials that exist before any tenant is
/// known — the webhook must verify its caller (Meta) before it can even ask
/// "which tenant". Never accepts a tenant identifier.
/// </summary>
public interface IWhatsAppWebhookCredentialProvider
{
    Task<string?> GetAppSecretAsync(CancellationToken cancellationToken);

    Task<string?> GetVerifyTokenAsync(CancellationToken cancellationToken);
}
