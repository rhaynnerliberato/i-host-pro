namespace IHostPro.Contexts.ExternalIntegrations.Api.Contracts;

public sealed record AirbnbAutoPublicationStatusResponse(Guid TenantId, bool AutoPublishEnabled, DateTimeOffset? AutoPublishNotBeforeUtc);
