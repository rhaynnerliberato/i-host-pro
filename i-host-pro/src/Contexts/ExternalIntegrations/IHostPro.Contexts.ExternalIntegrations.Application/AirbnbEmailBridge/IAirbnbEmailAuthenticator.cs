namespace IHostPro.Contexts.ExternalIntegrations.Application.AirbnbEmailBridge;

/// <summary>
/// Performs the Airbnb Email Bridge's interactive Microsoft account
/// authentication (MSAL, public client + PKCE, system browser) for a
/// tenant's mailbox connection. Persists the resulting token cache via
/// <see cref="IAirbnbEmailTokenCacheStore"/> itself — callers only ever see
/// the non-secret account identity in <see cref="AirbnbEmailAuthenticationOutcome"/>,
/// never token material.
///
/// Local-first only: this opens a system browser on the machine running the
/// calling process, so it is meant to be invoked from a developer's own
/// local Api instance, not a shared/cloud-hosted one (Fase 9 review item 21
/// — production OAuth UX is a separate, future architecture decision).
/// </summary>
public interface IAirbnbEmailAuthenticator
{
    Task<AirbnbEmailAuthenticationOutcome> ConnectInteractiveAsync(Guid tenantId, CancellationToken cancellationToken);
}
