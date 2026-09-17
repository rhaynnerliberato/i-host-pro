namespace IHostPro.Contexts.ExternalIntegrations.Api.Contracts;

/// <summary>The Microsoft authorization URL to navigate the browser to — never the state or PKCE material (both stay server-side).</summary>
public sealed record AirbnbEmailWebOAuthStartResponse(string AuthorizationUrl);
