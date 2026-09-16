using IHostPro.BuildingBlocks.Application;

namespace IHostPro.Contexts.ExternalIntegrations.Application.AirbnbListingTitleMappings;

public sealed record ListAirbnbListingTitleMappingsQuery(Guid TenantId) : IQuery<IReadOnlyList<AirbnbListingTitleMappingResult>>;
