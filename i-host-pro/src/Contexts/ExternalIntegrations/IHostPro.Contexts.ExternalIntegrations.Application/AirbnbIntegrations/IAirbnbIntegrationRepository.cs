using IHostPro.BuildingBlocks.Domain;
using IHostPro.Contexts.ExternalIntegrations.Domain;

namespace IHostPro.Contexts.ExternalIntegrations.Application.AirbnbIntegrations;

/// <summary>
/// Application-facing repository for <see cref="AirbnbIntegration"/> —
/// mirrors <c>IWhatsAppIntegrationRepository</c> exactly: exactly one row per
/// tenant (see <c>AirbnbIntegrationConfiguration</c>'s unique index). Tenant
/// scoping is handled transparently by <c>BaseDbContext</c>'s Global Query
/// Filter + RLS.
/// </summary>
public interface IAirbnbIntegrationRepository : IRepository<AirbnbIntegration, Guid>
{
    Task<AirbnbIntegration?> GetForCurrentTenantAsync(CancellationToken cancellationToken);
}
