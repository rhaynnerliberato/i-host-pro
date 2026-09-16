namespace IHostPro.Contexts.ExternalIntegrations.Application.AirbnbEmailBridge;

/// <summary>Automatic Publication Design + Safety gate — never carries mailbox address, listing title, or any guest/reservation data.</summary>
public sealed record AirbnbAutoPublicationStatusResult(Guid TenantId, bool AutoPublishEnabled, DateTimeOffset? AutoPublishNotBeforeUtc)
{
    public static AirbnbAutoPublicationStatusResult NotConfigured(Guid tenantId) => new(tenantId, false, null);
}
