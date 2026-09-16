namespace IHostPro.Contexts.ExternalIntegrations.Api.Contracts;

/// <summary>
/// <see cref="NotBeforeUtc"/> is deliberately required at this boundary
/// (nullable so a caller that omits it gets a precise 400, never a silent
/// default) - Automatic Publication Design + Safety gate item 10.
/// </summary>
public sealed record EnableAirbnbAutoPublicationRequest(DateTimeOffset? NotBeforeUtc);
