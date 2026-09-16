using IHostPro.BuildingBlocks.Application;

namespace IHostPro.Contexts.ExternalIntegrations.Application.AirbnbEmailBridge;

/// <summary>
/// Turns automatic publication back off. Touches nothing else - the
/// Microsoft mailbox connection, token cache, listing-title mappings,
/// receipts, and any already-created reservations all remain untouched
/// (mirrors <see cref="DisconnectAirbnbEmailMailboxCommand"/>'s own "never
/// delete historical record" principle).
/// </summary>
public sealed record DisableAirbnbAutoPublicationCommand(Guid TenantId, Guid ActorUserId) : ICommand<AirbnbAutoPublicationStatusResult>;
