using IHostPro.BuildingBlocks.Application;

namespace IHostPro.Contexts.ExternalIntegrations.Application.AirbnbEmailBridge;

/// <summary>
/// Disables the tenant's Airbnb Email Bridge mailbox connection and discards
/// its encrypted token cache. Retains the connection row, account identity,
/// and every message receipt as historical record (Fase 9 review §21) —
/// never deletes anything.
/// </summary>
public sealed record DisconnectAirbnbEmailMailboxCommand(Guid TenantId, Guid ActorUserId) : ICommand<AirbnbEmailMailboxConnectionResult>;
