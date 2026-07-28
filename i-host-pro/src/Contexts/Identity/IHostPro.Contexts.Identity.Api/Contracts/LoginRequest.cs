namespace IHostPro.Contexts.Identity.Api.Contracts;

/// <summary>
/// Public request body for <c>POST /api/v1/auth/login</c> (Incremento 2 plan,
/// Etapa 14). Deliberately carries only these three fields — no IP, device,
/// browser, tenant id, user id, or session id: those are either captured
/// internally from <c>HttpContext</c> by the controller or never accepted
/// from a client at all. Properties are nullable so a missing/null JSON
/// field never triggers ASP.NET Core's own automatic model-validation 400
/// (which would bypass <c>ValidationBehavior</c>'s ASCII error codes) — an
/// absent value flows through to <c>LoginCommandValidator</c>'s
/// <c>NotEmpty()</c> rule instead, producing the same stable code as any
/// other empty value.
/// </summary>
public sealed record LoginRequest(string? TenantSlug, string? Email, string? Password)
{
    public override string ToString() =>
        $"{nameof(LoginRequest)} {{ {nameof(TenantSlug)} = {TenantSlug}, {nameof(Email)} = [REDACTED], " +
        $"{nameof(Password)} = [REDACTED] }}";
}
