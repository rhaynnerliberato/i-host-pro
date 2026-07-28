using System.Security.Claims;
using IHostPro.BuildingBlocks.Infrastructure.Multitenancy;
using IHostPro.Contexts.Identity.Application;
using IHostPro.Contexts.Identity.Infrastructure.Security;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace IHostPro.Contexts.Identity.Infrastructure.Authentication;

/// <summary>
/// Configures the <c>"Bearer"</c> scheme's <see cref="JwtBearerOptions"/>
/// (Incremento 2 plan, Etapa 13; ADR-012). Registered as
/// <see cref="IConfigureNamedOptions{TOptions}"/> so its dependencies
/// (<see cref="IJwtSigningKeyProvider"/>, <see cref="IOptions{TOptions}"/> of
/// <see cref="JwtOptions"/> — both Singleton-safe) are supplied by the DI
/// container when the Options infrastructure builds this instance, never by
/// calling <c>BuildServiceProvider()</c> inside <c>AddJwtBearer(...)</c>
/// itself. <see cref="ISessionRevocationCache"/>/<see cref="ITenantContext"/>
/// (both Scoped) are deliberately NOT constructor-injected here — that would
/// be the same captive-dependency defect fixed for <c>DummyPasswordVerifier</c>
/// (Etapa 9 -&gt; 10). They are resolved per-request from
/// <c>HttpContext.RequestServices</c> inside <see cref="OnTokenValidatedAsync"/>
/// instead, exactly as ASP.NET Core authentication events are meant to reach
/// scoped services.
/// </summary>
public sealed class ConfigureJwtBearerOptions : IConfigureNamedOptions<JwtBearerOptions>
{
    private readonly IJwtSigningKeyProvider _signingKeyProvider;
    private readonly IOptions<JwtOptions> _jwtOptions;

    public ConfigureJwtBearerOptions(IJwtSigningKeyProvider signingKeyProvider, IOptions<JwtOptions> jwtOptions)
    {
        _signingKeyProvider = signingKeyProvider;
        _jwtOptions = jwtOptions;
    }

    public void Configure(string? name, JwtBearerOptions options) => Configure(options);

    public void Configure(JwtBearerOptions options)
    {
        var jwtOptions = _jwtOptions.Value;

        // MapInboundClaims = false: claim types must stay exactly as
        // JwtTokenGenerator wrote them ("sub", "tenant_id", "session_id",
        // "jti", "role") — the legacy inbound-claim mapping (rewriting "sub"
        // to the long http://schemas...nameidentifier URI, etc.) would
        // silently break every downstream claim-type comparison here and in
        // any future authorization policy.
        options.MapInboundClaims = false;
        options.IncludeErrorDetails = false; // never leak validation-failure specifics in the response

        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = jwtOptions.Issuer,

            ValidateAudience = true,
            ValidAudience = jwtOptions.Audience,

            ValidateLifetime = true,
            ClockSkew = jwtOptions.ClockSkew,

            ValidateIssuerSigningKey = true,
            // RS256 exclusively — rejects "alg: none" and any algorithm
            // downgrade/confusion attempt (e.g. HS256 using the public RSA
            // modulus as an HMAC secret), a well-known JWT vulnerability
            // class.
            ValidAlgorithms = [SecurityAlgorithms.RsaSha256],
            IssuerSigningKeyResolver = ResolveSigningKeys,

            NameClaimType = JwtRegisteredClaimNames.Sub,
            RoleClaimType = RoleClaimType,
        };

        options.Events = new JwtBearerEvents
        {
            OnTokenValidated = OnTokenValidatedAsync,
        };
    }

    /// <summary>
    /// Rejects a missing or unknown <c>kid</c> by construction: an empty
    /// header key id, or one that matches none of
    /// <see cref="IJwtSigningKeyProvider.GetValidationKeys"/>, yields an
    /// empty key set here, which the token handler treats as "no matching
    /// signing key found" — signature validation then fails the same way it
    /// would for a wrong key.
    /// </summary>
    private IEnumerable<SecurityKey> ResolveSigningKeys(
        string token, SecurityToken? securityToken, string? kid, TokenValidationParameters validationParameters)
    {
        if (string.IsNullOrEmpty(kid))
            return [];

        return _signingKeyProvider.GetValidationKeys()
            .Where(key => key.KeyId == kid)
            .Select(key => (SecurityKey)key.SecurityKey)
            .ToArray();
    }

    private static async Task OnTokenValidatedAsync(TokenValidatedContext context)
    {
        var principal = context.Principal!;

        if (!TryGetExactlyOneCanonicalGuidClaim(principal, JwtRegisteredClaimNames.Sub, out _) ||
            !TryGetExactlyOneCanonicalGuidClaim(principal, TenantIdClaimType, out var tenantId) ||
            !TryGetExactlyOneCanonicalGuidClaim(principal, SessionIdClaimType, out var sessionId) ||
            !TryGetExactlyOneCanonicalGuidClaim(principal, JwtRegisteredClaimNames.Jti, out _))
        {
            context.Fail(InvalidTokenFailureReason);
            return;
        }

        // Redis only — never PostgreSQL on an authenticated request. Failure
        // to reach Redis is already handled inside ISessionRevocationCache
        // itself (fail-open, returns false) — this call never throws for a
        // cache-layer problem, only for the caller's own cancellation.
        var revocationCache = context.HttpContext.RequestServices.GetRequiredService<ISessionRevocationCache>();
        var isRevoked = await revocationCache.IsRevokedAsync(tenantId, sessionId, context.HttpContext.RequestAborted);
        if (isRevoked)
        {
            context.Fail(InvalidTokenFailureReason);
            return;
        }

        // Only reached once every prior check succeeded.
        var tenantContext = context.HttpContext.RequestServices.GetRequiredService<ITenantContext>();
        tenantContext.SetTenant(tenantId);
    }

    /// <summary>
    /// True only when exactly one claim of <paramref name="claimType"/>
    /// exists and its value is a GUID in the exact canonical hyphenated form
    /// (<c>Guid.ToString()</c>'s default "D" format — what
    /// <c>JwtTokenGenerator</c> itself always emits). A duplicated claim
    /// (two entries of the same type, however produced) or any other
    /// GUID-like variant is rejected, never silently resolved via
    /// <c>FindFirst</c>.
    /// </summary>
    private static bool TryGetExactlyOneCanonicalGuidClaim(ClaimsPrincipal principal, string claimType, out Guid value)
    {
        value = Guid.Empty;

        var matches = principal.Claims.Where(c => c.Type == claimType).ToArray();
        if (matches.Length != 1)
            return false;

        return Guid.TryParseExact(matches[0].Value, "D", out value);
    }

    private const string InvalidTokenFailureReason = "invalid_token";
    private const string RoleClaimType = "role";
    private const string TenantIdClaimType = "tenant_id";
    private const string SessionIdClaimType = "session_id";
}
