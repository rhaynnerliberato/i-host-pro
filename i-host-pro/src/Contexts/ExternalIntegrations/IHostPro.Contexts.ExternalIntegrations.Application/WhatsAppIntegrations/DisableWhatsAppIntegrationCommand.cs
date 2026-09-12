using IHostPro.BuildingBlocks.Application;

namespace IHostPro.Contexts.ExternalIntegrations.Application.WhatsAppIntegrations;

/// <summary>
/// Explicitly deactivates the tenant's WhatsApp integration (Real Tenant
/// WhatsApp Activation Readiness gate — SMALL_IMPLEMENTATION_GAP plan).
/// Mirrors <see cref="EnableWhatsAppIntegrationCommand"/>'s own shape.
/// </summary>
public sealed record DisableWhatsAppIntegrationCommand(Guid TenantId, Guid ActorUserId) : ICommand<WhatsAppIntegrationResult>;
