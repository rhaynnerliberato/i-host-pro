namespace IHostPro.Contexts.ExternalIntegrations.Api.Contracts;

public sealed record CreateAirbnbListingTitleMappingRequest(string ListingTitle, Guid PropertyId);
