namespace IHostPro.Contexts.Identity.Application;

/// <summary>
/// The token pair returned by a successful login or refresh (Incremento 2
/// plan, Etapa 8). <see cref="TokenType"/> is always <c>"Bearer"</c> (RFC
/// 6750) — fixed, never a settable constructor parameter, so it can never be
/// accidentally constructed with a different value.
///
/// <see cref="AccessTokenExpiresAt"/>/<see cref="RefreshTokenExpiresAt"/>
/// MUST be UTC — enforced in the constructor (<c>Offset == TimeSpan.Zero</c>),
/// not merely documented, so a caller that accidentally passes a local-time
/// <see cref="DateTimeOffset"/> fails immediately instead of producing a
/// result with an ambiguous expiration.
///
/// <see cref="AccessToken"/> and <see cref="RefreshToken"/> are excluded
/// from <see cref="ToString"/> (overridden below, replacing the record's
/// compiler-generated one) — this is the type an HTTP response will
/// eventually be built from, so it is exactly the kind of object a
/// structured-logging call or an unguarded breakpoint is likely to
/// stringify by accident.
/// </summary>
public sealed record AuthTokensResult
{
    public string AccessToken { get; }
    public DateTimeOffset AccessTokenExpiresAt { get; }
    public string RefreshToken { get; }
    public DateTimeOffset RefreshTokenExpiresAt { get; }
    public string TokenType => "Bearer";

    public AuthTokensResult(
        string accessToken, DateTimeOffset accessTokenExpiresAt, string refreshToken, DateTimeOffset refreshTokenExpiresAt)
    {
        ArgumentException.ThrowIfNullOrEmpty(accessToken);
        ArgumentException.ThrowIfNullOrEmpty(refreshToken);

        if (accessTokenExpiresAt.Offset != TimeSpan.Zero)
            throw new ArgumentException("Access token expiration must be UTC.", nameof(accessTokenExpiresAt));

        if (refreshTokenExpiresAt.Offset != TimeSpan.Zero)
            throw new ArgumentException("Refresh token expiration must be UTC.", nameof(refreshTokenExpiresAt));

        AccessToken = accessToken;
        AccessTokenExpiresAt = accessTokenExpiresAt;
        RefreshToken = refreshToken;
        RefreshTokenExpiresAt = refreshTokenExpiresAt;
    }

    public override string ToString() =>
        $"{nameof(AuthTokensResult)} {{ {nameof(AccessToken)} = [REDACTED], " +
        $"{nameof(AccessTokenExpiresAt)} = {AccessTokenExpiresAt:O}, {nameof(RefreshToken)} = [REDACTED], " +
        $"{nameof(RefreshTokenExpiresAt)} = {RefreshTokenExpiresAt:O}, {nameof(TokenType)} = {TokenType} }}";
}
