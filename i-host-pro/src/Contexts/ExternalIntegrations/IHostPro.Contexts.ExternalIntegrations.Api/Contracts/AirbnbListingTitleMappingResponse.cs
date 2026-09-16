namespace IHostPro.Contexts.ExternalIntegrations.Api.Contracts;

public sealed record AirbnbListingTitleMappingResponse(
    Guid Id,
    Guid TenantId,
    string ListingTitle,
    Guid PropertyId,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? UpdatedAtUtc);
