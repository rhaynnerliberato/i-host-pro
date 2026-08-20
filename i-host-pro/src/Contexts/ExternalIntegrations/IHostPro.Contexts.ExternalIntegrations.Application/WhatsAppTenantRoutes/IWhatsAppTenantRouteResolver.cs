namespace IHostPro.Contexts.ExternalIntegrations.Application.WhatsAppTenantRoutes;

/// <summary>
/// Read-only, pre-tenant-resolution lookup used by the webhook ingress
/// AFTER signature verification (Fase 9, Checkpoint 2.3.2 — ADR-022 item
/// 10). Deliberately narrower than <see cref="IWhatsAppTenantRouteRepository"/>
/// — returns only an opaque <see cref="Guid"/>, never the
/// <c>WhatsAppTenantRoute</c> entity or a <c>DbContext</c>, so the Api layer
/// never needs to reference <c>ExternalIntegrations.Domain</c> directly.
/// </summary>
public interface IWhatsAppTenantRouteResolver
{
    Task<Guid?> ResolveTenantIdAsync(string phoneNumberId, CancellationToken cancellationToken);
}
