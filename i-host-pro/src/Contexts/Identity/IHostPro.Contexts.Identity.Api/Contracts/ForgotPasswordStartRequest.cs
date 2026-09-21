namespace IHostPro.Contexts.Identity.Api.Contracts;

/// <summary>
/// Public request body for <c>POST /api/v1/auth/forgot-password/start</c>
/// (Self-Service Identity &amp; Onboarding Foundation gate). <c>TenantSlug</c>
/// stays required for this first version — a temporary UX limitation,
/// approved: resolving an account across tenants from an email alone would
/// itself be a cross-tenant enumeration surface.
/// </summary>
public sealed record ForgotPasswordStartRequest(string? TenantSlug, string? Email)
{
    public override string ToString() =>
        $"{nameof(ForgotPasswordStartRequest)} {{ {nameof(TenantSlug)} = {TenantSlug}, {nameof(Email)} = [REDACTED] }}";
}
