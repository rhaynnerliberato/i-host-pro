namespace IHostPro.Contexts.Identity.Api.Contracts;

/// <summary>
/// Public response body for a successful login or refresh (Incremento 2
/// plan, Etapa 14) — this project's own DTO, mapped from the
/// Application-layer <c>AuthTokensResult</c>, never that type serialized
/// directly ("DTOs públicos próprios"). Property names are camelCase on the
/// wire via the host's default JSON naming policy.
/// </summary>
public sealed record AuthTokensResponse(
    string AccessToken,
    DateTimeOffset AccessTokenExpiresAt,
    string RefreshToken,
    DateTimeOffset RefreshTokenExpiresAt,
    string TokenType)
{
    public override string ToString() =>
        $"{nameof(AuthTokensResponse)} {{ {nameof(AccessToken)} = [REDACTED], " +
        $"{nameof(AccessTokenExpiresAt)} = {AccessTokenExpiresAt:O}, {nameof(RefreshToken)} = [REDACTED], " +
        $"{nameof(RefreshTokenExpiresAt)} = {RefreshTokenExpiresAt:O}, {nameof(TokenType)} = {TokenType} }}";
}
