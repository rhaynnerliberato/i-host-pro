using IHostPro.BuildingBlocks.Application;

namespace IHostPro.Contexts.ExternalIntegrations.Application.WhatsAppIntegrations;

/// <summary>
/// Explicitly activates the tenant's already-configured WhatsApp
/// integration (Real Tenant WhatsApp Activation Readiness gate —
/// SMALL_IMPLEMENTATION_GAP plan). Deliberately a separate command from
/// <see cref="ConfigureWhatsAppIntegrationCommand"/> — Configure never
/// implicitly enables, mirroring that command's own established pattern of
/// carrying <see cref="ActorUserId"/> for
/// <see cref="AuditEnableWhatsAppIntegrationBehavior"/>.
/// </summary>
public sealed record EnableWhatsAppIntegrationCommand(Guid TenantId, Guid ActorUserId) : ICommand<WhatsAppIntegrationResult>;
