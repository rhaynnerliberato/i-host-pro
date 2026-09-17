namespace IHostPro.Contexts.ExternalIntegrations.Application.AirbnbEmailBridge;

/// <summary>
/// Orchestrates the <c>oauth/callback</c> bootstrap sequence (Web OAuth
/// architecture gate, items 13-17): consume the state hash with NO ambient
/// tenant context, recover the trusted tenant/user binding, THEN establish
/// tenant context, THEN complete the token exchange under normal tenant-
/// scoped RLS. Deliberately bypasses the Mediator/command-dispatch pipeline
/// entirely (<c>IExternalIntegrationsRequestDispatcher</c>) — every pipeline
/// behavior in this codebase assumes tenant context is ALREADY resolved
/// before the handler runs, which is never true for this one request shape.
/// The Api controller calls this directly, exactly like it calls
/// <c>IAirbnbEmailAuthenticator</c> directly for other flows.
/// </summary>
public interface IAirbnbEmailWebOAuthCallbackProcessor
{
    Task<AirbnbEmailWebOAuthCallbackReport> ProcessAsync(
        string? state, string? authorizationCode, string? microsoftError, CancellationToken cancellationToken);
}

/// <summary>
/// <see cref="FrontendReturnUrl"/> is resolved from configuration by the
/// Infrastructure implementation (the Api layer never references
/// Infrastructure directly, so it cannot read <c>AirbnbEmailBridgeOptions</c>
/// itself) — the controller only ever appends its own bounded result-code
/// query parameter to it, never anything else. Only <c>null</c> when
/// <see cref="Result"/> is <see cref="AirbnbEmailWebOAuthCallbackResult.NotConfigured"/>.
/// </summary>
public sealed record AirbnbEmailWebOAuthCallbackReport(AirbnbEmailWebOAuthCallbackResult Result, string? FrontendReturnUrl);

/// <summary>
/// A single, deliberately coarse-grained outcome enum — the Api controller
/// maps every value to a bounded, non-sensitive frontend query parameter
/// (item 21 of the gate) and NOTHING else ever reaches the browser: no raw
/// Microsoft error text, no distinction between "state not found" and
/// "state already consumed" (item 9's repository contract already collapses
/// those two on purpose).
/// </summary>
public enum AirbnbEmailWebOAuthCallbackResult
{
    Success,
    UserCancelledOrConsentDenied,
    InvalidOrExpiredState,
    ExchangeFailed,

    /// <summary>
    /// Web OAuth was never configured in this environment at all (no
    /// WebFrontendReturnUrl) — there is no valid frontend URL to redirect to,
    /// so the controller returns a plain error status instead of a redirect.
    /// A pending transaction can only exist once oauth/start's own
    /// configuration check has already passed, so this can only happen for a
    /// callback request that was never preceded by a real oauth/start call.
    /// </summary>
    NotConfigured,
}
