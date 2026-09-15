namespace IHostPro.Contexts.ExternalIntegrations.Domain;

/// <summary>
/// Connectivity state of a tenant's <see cref="AirbnbEmailMailboxConnection"/>
/// to its Microsoft Graph mailbox (Airbnb Email Bridge foundation). The real
/// OAuth flow that transitions a connection into <see cref="Connected"/> is a
/// later, separately authorized gate — this enum only models the states the
/// persistence layer already needs to represent.
/// </summary>
public enum AirbnbEmailAuthorizationStatus
{
    Disconnected = 0,
    Connected = 1,
    Error = 2,
}
