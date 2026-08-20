using IHostPro.Contexts.ExternalIntegrations.Domain;

namespace IHostPro.Contexts.ExternalIntegrations.Application.WhatsAppTenantRoutes;

/// <summary>
/// Write-side access to the global (non-tenant-owned) routing directory
/// (Fase 9, Checkpoint 2.3.2). <see cref="GetByPhoneNumberIdAsync"/>
/// deliberately takes no tenant context — this is the one lookup in the
/// whole Bounded Context that must work before any <c>TenantId</c> is known.
/// </summary>
public interface IWhatsAppTenantRouteRepository
{
    Task<WhatsAppTenantRoute?> GetByTenantIdAsync(Guid tenantId, CancellationToken cancellationToken);

    Task<WhatsAppTenantRoute?> GetByPhoneNumberIdAsync(string phoneNumberId, CancellationToken cancellationToken);

    void Add(WhatsAppTenantRoute route);

    void Remove(WhatsAppTenantRoute route);
}
