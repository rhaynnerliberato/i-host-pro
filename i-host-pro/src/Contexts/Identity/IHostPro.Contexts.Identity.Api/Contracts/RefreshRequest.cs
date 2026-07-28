namespace IHostPro.Contexts.Identity.Api.Contracts;

/// <summary>
/// Public request body for <c>POST /api/v1/auth/refresh</c> (Incremento 2
/// plan, Etapa 14). Carries only the refresh token — no IP, device, browser,
/// tenant id, user id, or session id (the tenant is derived from the token
/// itself; user/session are derived from the stored token row).
/// </summary>
public sealed record RefreshRequest(string? RefreshToken)
{
    public override string ToString() =>
        $"{nameof(RefreshRequest)} {{ {nameof(RefreshToken)} = [REDACTED] }}";
}
