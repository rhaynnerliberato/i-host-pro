namespace IHostPro.Contexts.Identity.Api.Contracts;

/// <summary>
/// Public response body for a successful signup (Self-Service Identity &amp;
/// Onboarding Foundation gate) — the generated tenant slug (so the new admin
/// knows which "empresa" to log into later) plus a normal issued session,
/// identical in shape to a successful login.
/// </summary>
public sealed record SignupResponse(string TenantSlug, AuthTokensResponse Tokens);
