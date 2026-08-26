using IHostPro.BuildingBlocks.Domain;
using IHostPro.Contexts.ExternalIntegrations.Domain;

namespace IHostPro.Contexts.ExternalIntegrations.Application.AirbnbListingMappings;

/// <summary>
/// Application-facing repository for <see cref="AirbnbListingMapping"/> —
/// mirrors <c>IWhatsAppTemplateMappingRepository</c>'s shape. Tenant scoping
/// is handled transparently by <c>BaseDbContext</c>'s Global Query Filter +
/// RLS.
/// </summary>
public interface IAirbnbListingMappingRepository : IRepository<AirbnbListingMapping, Guid>
{
    /// <summary>
    /// The current tenant's mapping for <paramref name="externalListingId"/>,
    /// or <c>null</c> if this listing has never been mapped — the case an
    /// import publisher must treat as "cannot resolve PropertyId, do not
    /// publish" (CP3.2 mandate §3).
    /// </summary>
    Task<AirbnbListingMapping?> GetByExternalListingIdAsync(string externalListingId, CancellationToken cancellationToken);
}
