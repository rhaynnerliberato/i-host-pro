using IHostPro.BuildingBlocks.Domain;
using IHostPro.Contexts.ExternalIntegrations.Domain;

namespace IHostPro.Contexts.ExternalIntegrations.Application.AirbnbEmailBridge;

/// <summary>
/// Application-facing repository for <see cref="AirbnbEmailMailboxConnection"/>
/// — mirrors <c>IAirbnbIntegrationRepository</c> exactly: exactly one row per
/// tenant (see <c>AirbnbEmailMailboxConnectionConfiguration</c>'s unique
/// index). Tenant scoping is handled transparently by <c>BaseDbContext</c>'s
/// Global Query Filter + RLS.
/// </summary>
public interface IAirbnbEmailMailboxConnectionRepository : IRepository<AirbnbEmailMailboxConnection, Guid>
{
    Task<AirbnbEmailMailboxConnection?> GetForCurrentTenantAsync(CancellationToken cancellationToken);
}
