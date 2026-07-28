using IHostPro.Contexts.Identity.Application;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace IHostPro.Contexts.Identity.Infrastructure.Security;

/// <summary>
/// Issues RS256-signed, stateless access tokens (ADR-012, Incremento 2 plan,
/// Etapa 6). Depends only on <see cref="IJwtSigningKeyProvider"/>,
/// <see cref="IOptions{TOptions}"/> of <see cref="JwtOptions"/> and a
/// <see cref="TimeProvider"/> — never <c>HttpContext</c>, EF Core, or Redis
/// (Application/Infrastructure boundary; this class has no reason to know
/// about persistence or the current HTTP request).
///
/// Deterministic given its inputs and configuration, except for
/// <see cref="JwtAccessTokenResult.Jti"/> (a fresh random value per call, by
/// design — two tokens for the same request must never share an id) and the
/// current time. Safe for concurrent use: <see cref="JsonWebTokenHandler"/>
/// and the cached <see cref="JwtSigningKey"/>/<see cref="SigningCredentials"/>
/// are stateless for token creation, and nothing here mutates shared state —
/// the RSA key itself is imported once by <see cref="IJwtSigningKeyProvider"/>,
/// never per token.
///
/// The generated access token is returned to the caller and never persisted,
/// logged, or otherwise recorded by this class.
/// </summary>
public sealed class JwtTokenGenerator : IJwtTokenGenerator
{
    private readonly IJwtSigningKeyProvider _signingKeyProvider;
    private readonly IOptions<JwtOptions> _options;
    private readonly TimeProvider _timeProvider;
    private readonly JsonWebTokenHandler _handler = new();

    public JwtTokenGenerator(IJwtSigningKeyProvider signingKeyProvider, IOptions<JwtOptions> options, TimeProvider timeProvider)
    {
        _signingKeyProvider = signingKeyProvider;
        _options = options;
        _timeProvider = timeProvider;
    }

    public JwtAccessTokenResult GenerateAccessToken(JwtAccessTokenRequest request)
    {
        var options = _options.Value;
        var now = _timeProvider.GetUtcNow().UtcDateTime;
        var expiresAt = now.Add(options.AccessTokenLifetime);
        var jti = Guid.NewGuid().ToString();
        var signingKey = _signingKeyProvider.GetCurrentSigningKey();

        var claims = new Dictionary<string, object>
        {
            [JwtRegisteredClaimNames.Sub] = request.UserId.ToString(),
            ["tenant_id"] = request.TenantId.ToString(),
            ["session_id"] = request.SessionId.ToString(),
            [JwtRegisteredClaimNames.Jti] = jti,
            // A JSON array even for a single role — the "role" claim always
            // represents "every role this user holds", never a bare scalar
            // that would need special-casing by a consumer.
            ["role"] = request.Roles.ToArray(),
        };

        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = options.Issuer,
            Audience = options.Audience,
            IssuedAt = now,
            NotBefore = now,
            Expires = expiresAt,
            Claims = claims,
            SigningCredentials = signingKey.SigningCredentials,
        };

        var token = _handler.CreateToken(descriptor);

        return new JwtAccessTokenResult(token, new DateTimeOffset(expiresAt, TimeSpan.Zero), jti);
    }
}
