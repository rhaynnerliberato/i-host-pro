using IHostPro.BuildingBlocks.Application;

namespace IHostPro.Contexts.ExternalIntegrations.Application.AirbnbEmailBridge;

public sealed record GetAirbnbEmailBridgeStatusQuery(Guid TenantId) : IQuery<AirbnbEmailBridgeStatusResult>;
