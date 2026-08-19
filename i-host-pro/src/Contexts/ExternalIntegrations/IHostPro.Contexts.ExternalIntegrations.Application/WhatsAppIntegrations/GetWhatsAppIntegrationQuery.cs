using IHostPro.BuildingBlocks.Application;

namespace IHostPro.Contexts.ExternalIntegrations.Application.WhatsAppIntegrations;

/// <summary>
/// Returns the tenant's WhatsApp integration configuration — never a secret
/// value, only whether each secret reference is configured. Returns
/// <see cref="WhatsAppIntegrationResult.NotConfigured"/> when the tenant has
/// never configured one yet; this is not an error.
/// </summary>
public sealed record GetWhatsAppIntegrationQuery(Guid TenantId) : IQuery<WhatsAppIntegrationResult>;
