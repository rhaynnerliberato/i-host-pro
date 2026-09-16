using IHostPro.BuildingBlocks.Application;

namespace IHostPro.Contexts.ExternalIntegrations.Application.AirbnbEmailBridge;

public sealed record GetAirbnbAutoPublicationStatusQuery(Guid TenantId) : IQuery<AirbnbAutoPublicationStatusResult>;
