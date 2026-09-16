using IHostPro.BuildingBlocks.Application;

namespace IHostPro.Contexts.ExternalIntegrations.Application.AirbnbEmailBridge;

/// <summary>
/// Turns on automatic publication for the current tenant's Airbnb Email
/// Bridge (Automatic Publication Design + Safety gate). <see cref="NotBeforeUtc"/>
/// is deliberately nullable at this boundary, not defaulted - a caller that
/// omits it is rejected with <see cref="AirbnbEmailBridgeErrorCodes.AutoPublicationCutoffRequired"/>,
/// never silently treated as "publish from the mailbox's entire history."
/// </summary>
public sealed record EnableAirbnbAutoPublicationCommand(Guid TenantId, Guid ActorUserId, DateTimeOffset? NotBeforeUtc)
    : ICommand<AirbnbAutoPublicationStatusResult>;
