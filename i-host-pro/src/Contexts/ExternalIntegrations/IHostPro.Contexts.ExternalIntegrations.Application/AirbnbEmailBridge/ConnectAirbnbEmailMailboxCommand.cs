using IHostPro.BuildingBlocks.Application;

namespace IHostPro.Contexts.ExternalIntegrations.Application.AirbnbEmailBridge;

/// <summary>
/// Initiates (or re-runs, for a reconnect) the interactive Microsoft
/// authentication that connects the tenant's Airbnb Email Bridge mailbox.
/// Local-first only — opens a system browser on the machine running the
/// calling Api process.
/// </summary>
public sealed record ConnectAirbnbEmailMailboxCommand(Guid TenantId, Guid ActorUserId) : ICommand<AirbnbEmailMailboxConnectionResult>;
