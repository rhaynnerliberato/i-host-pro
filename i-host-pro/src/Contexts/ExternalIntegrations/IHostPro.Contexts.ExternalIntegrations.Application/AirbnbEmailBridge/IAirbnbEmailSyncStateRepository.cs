using IHostPro.BuildingBlocks.Domain;
using IHostPro.Contexts.ExternalIntegrations.Domain;

namespace IHostPro.Contexts.ExternalIntegrations.Application.AirbnbEmailBridge;

/// <summary>Application-facing repository for <see cref="AirbnbEmailSyncState"/> — tenant scoping handled by <c>BaseDbContext</c>'s Global Query Filter + RLS.</summary>
public interface IAirbnbEmailSyncStateRepository : IRepository<AirbnbEmailSyncState, Guid>
{
    Task<AirbnbEmailSyncState?> GetForCurrentTenantAsync(Guid mailboxConnectionId, string mailFolderId, CancellationToken cancellationToken);
}
