using IHostPro.BuildingBlocks.Domain;
using IHostPro.Contexts.ExternalIntegrations.Domain;

namespace IHostPro.Contexts.ExternalIntegrations.Application.AirbnbListingTitleMappings;

/// <summary>
/// Application-facing repository for <see cref="AirbnbListingTitleMapping"/> —
/// mirrors <c>IAirbnbListingMappingRepository</c>'s shape. Tenant scoping is
/// handled transparently by <c>BaseDbContext</c>'s Global Query Filter + RLS.
/// </summary>
public interface IAirbnbListingTitleMappingRepository : IRepository<AirbnbListingTitleMapping, Guid>
{
    /// <summary>
    /// The current tenant's mapping for <paramref name="listingTitle"/>
    /// (normalized the same way <see cref="AirbnbListingTitleMapping.Normalize"/>
    /// does before comparing), or <c>null</c> if this title has never been
    /// mapped — the case a DRY_RUN evaluation must treat as "cannot resolve
    /// PropertyId, do not import".
    /// </summary>
    Task<AirbnbListingTitleMapping?> GetByListingTitleAsync(string listingTitle, CancellationToken cancellationToken);
}
