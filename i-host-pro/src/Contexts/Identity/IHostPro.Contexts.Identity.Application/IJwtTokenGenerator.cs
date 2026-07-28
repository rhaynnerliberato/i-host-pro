namespace IHostPro.Contexts.Identity.Application;

/// <summary>
/// Access-token roles requested for a single user, by stable role code (e.g.
/// "ADMIN") — multiple roles are supported (ADR-012). Never includes
/// permissions: those are resolved server-side per request from the role(s),
/// not embedded in the token (ADR-012).
/// </summary>
public sealed record JwtAccessTokenRequest(
    Guid UserId,
    Guid TenantId,
    Guid SessionId,
    IReadOnlyCollection<string> Roles);

/// <summary>
/// A freshly issued, stateless access token. Never persisted anywhere — the
/// caller uses <see cref="Token"/> only to return it to the client;
/// <see cref="Jti"/> is exposed solely for audit/log correlation (never the
/// token itself).
///
/// <see cref="Token"/> is deliberately excluded from <see cref="ToString"/>
/// (overridden below, replacing the record's compiler-generated one, which
/// would otherwise print every property including the token) — this also
/// covers accidental exposure via string interpolation, structured-logging
/// templates that stringify the whole object, and the debugger's default
/// display (Incremento 2 plan, Etapa 8).
/// </summary>
public sealed record JwtAccessTokenResult(string Token, DateTimeOffset ExpiresAt, string Jti)
{
    public override string ToString() =>
        $"{nameof(JwtAccessTokenResult)} {{ {nameof(Token)} = [REDACTED], {nameof(ExpiresAt)} = {ExpiresAt:O}, {nameof(Jti)} = {Jti} }}";
}

/// <summary>
/// Issues signed access tokens for an already-authenticated user
/// (Incremento 2 plan, Etapa 6). Implemented in Identity.Infrastructure —
/// Application depends only on this abstraction, never on the signing
/// mechanism or key material (Architecture Principles, Section 4).
/// </summary>
public interface IJwtTokenGenerator
{
    JwtAccessTokenResult GenerateAccessToken(JwtAccessTokenRequest request);
}
