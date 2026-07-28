namespace IHostPro.Contexts.Identity.Application;

/// <summary>
/// A freshly issued refresh token. <see cref="Token"/> is the full opaque
/// string — it exists only to be handed to the client immediately in the
/// response; it must never be persisted, cached, or logged anywhere.
/// <see cref="TokenId"/> and <see cref="TokenHash"/> are what actually get
/// stored on the <c>RefreshToken</c> aggregate.
///
/// <see cref="ExpiresAt"/> is computed by the Infrastructure implementation
/// (which already has legitimate access to <c>RefreshTokenOptions.Lifetime</c>)
/// and returned here — mirroring <c>JwtAccessTokenResult.ExpiresAt</c>
/// (Etapa 6) exactly — precisely so Application-layer callers (e.g.
/// <c>LoginCommandHandler</c>, Etapa 9) never need to depend on
/// Identity.Infrastructure's options types to know how long the token they
/// were just handed remains valid (Architecture Principles, Section 4).
///
/// <see cref="Token"/> and <see cref="TokenHash"/> are both excluded from
/// <see cref="ToString"/> (overridden below, replacing the record's
/// compiler-generated one) — <see cref="TokenHash"/> is not the secret
/// itself, but Etapa 7 already established it must never be logged either,
/// so it is redacted here for the same reason. <see cref="TokenId"/> and
/// <see cref="ExpiresAt"/> are not sensitive and remain visible for
/// diagnostics (Incremento 2 plan, Etapa 8/9).
/// </summary>
public sealed record GeneratedRefreshToken(string Token, Guid TokenId, string TokenHash, DateTimeOffset ExpiresAt)
{
    public override string ToString() =>
        $"{nameof(GeneratedRefreshToken)} {{ {nameof(Token)} = [REDACTED], {nameof(TokenId)} = {TokenId}, " +
        $"{nameof(TokenHash)} = [REDACTED], {nameof(ExpiresAt)} = {ExpiresAt:O} }}";
}

/// <summary>
/// Issues a new opaque refresh token for a tenant (Incremento 2 plan,
/// Etapa 7). Implemented in Identity.Infrastructure — Application depends
/// only on this abstraction, never on the random-generation mechanism.
/// </summary>
public interface IRefreshTokenGenerator
{
    GeneratedRefreshToken Generate(Guid tenantId);
}
