using IHostPro.BuildingBlocks.Messaging.Abstractions;

namespace IHostPro.BuildingBlocks.Infrastructure.Multitenancy;

/// <summary>
/// Wolverine middleware that resolves the scoped <see cref="ITenantContext"/>
/// for IHostPro.Worker from the TenantId carried by every Integration Event
/// envelope, before the actual handler runs — mirroring, for consumed
/// messages, the same responsibility an authentication middleware has for
/// HTTP requests in IHostPro.Api (Architecture Principles, Section 7).
/// Registered globally, filtered to message types assignable to
/// <see cref="IntegrationEvent"/> (see WolverineOptions registration in the
/// Host processes).
/// </summary>
public static class TenantResolutionMiddleware
{
    public static void Before(IntegrationEvent message, ITenantContext tenantContext)
    {
        tenantContext.SetTenant(message.TenantId);
    }
}
