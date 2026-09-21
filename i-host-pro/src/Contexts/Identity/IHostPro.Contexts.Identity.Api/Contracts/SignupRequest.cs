namespace IHostPro.Contexts.Identity.Api.Contracts;

/// <summary>
/// Public request body for <c>POST /api/v1/signup</c> (Self-Service Identity
/// &amp; Onboarding Foundation gate). Deliberately carries only
/// customer-supplied data — never a tenant id, slug, role, or permission
/// flag; those are always generated/assigned server-side.
/// </summary>
public sealed record SignupRequest(string? CompanyName, string? AdminFullName, string? AdminEmail, string? Password)
{
    public override string ToString() =>
        $"{nameof(SignupRequest)} {{ {nameof(CompanyName)} = {CompanyName}, {nameof(AdminFullName)} = {AdminFullName}, " +
        $"{nameof(AdminEmail)} = [REDACTED], {nameof(Password)} = [REDACTED] }}";
}
