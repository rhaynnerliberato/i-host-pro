namespace IHostPro.Contexts.ExternalIntegrations.Application.AirbnbListingTitleMappings;

public sealed record AirbnbListingTitleMappingResult(
    Guid Id,
    Guid TenantId,
    string ListingTitle,
    Guid PropertyId,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? UpdatedAtUtc);
