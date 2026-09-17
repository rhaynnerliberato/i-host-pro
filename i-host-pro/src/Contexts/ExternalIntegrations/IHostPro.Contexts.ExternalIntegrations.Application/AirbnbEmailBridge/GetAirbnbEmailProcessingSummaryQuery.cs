using IHostPro.BuildingBlocks.Application;

namespace IHostPro.Contexts.ExternalIntegrations.Application.AirbnbEmailBridge;

public sealed record GetAirbnbEmailProcessingSummaryQuery(Guid TenantId) : IQuery<AirbnbEmailProcessingSummaryResult>;
