namespace IHostPro.Contexts.ExternalIntegrations.Application.AirbnbEmailBridge;

/// <summary>
/// Read-model status for the Airbnb Email Bridge Minimal Operations/UX gate —
/// deliberately separate from the domain's <see cref="Domain.AirbnbEmailAuthorizationStatus"/>,
/// which only ever describes an EXISTING <see cref="Domain.AirbnbEmailMailboxConnection"/>
/// row. <see cref="NotConfigured"/> represents the absence of any connection
/// row at all (a tenant that has never connected a mailbox) — a distinct,
/// operator-meaningful state from <see cref="Disconnected"/> (a connection
/// existed and was later disconnected).
/// </summary>
public enum AirbnbEmailConnectionStatus
{
    NotConfigured = 0,
    Disconnected = 1,
    Connected = 2,
    Error = 3,
}
