using System.Security.Claims;

namespace IHostPro.Contexts.Identity.Api.Http;

/// <summary>
/// Canonical, single-source claim extraction for every controller action
/// that needs the caller's own identity (Incremento 2 plan, Etapa 14;
/// Incremento 3, Checkpoint 4) — mirrors <c>ConfigureJwtBearerOptions.OnTokenValidatedAsync</c>'s
/// own claim check exactly (duplicated deliberately — this project cannot
/// reference Infrastructure). Re-validated here even though the exact same
/// check already ran during token validation: <c>[Authorize]</c> only proves
/// the token signature/lifetime/issuer/audience were valid at that point,
/// never that THIS action can blindly trust <c>ClaimsPrincipal.Claims</c> for
/// its own use ("claims devem ser lidas após autenticação e validadas
/// novamente").
///
/// Extracted from <c>AuthController.Logout</c> (Incremento 3, Checkpoint 4) so
/// every self-service controller reuses the exact same validation instead of
/// each re-implementing it — <c>AuthController</c> itself was updated to call
/// this too, no behavior change.
/// </summary>
public static class AuthenticatedIdentityReader
{
    private const string SubClaimType = "sub";
    private const string TenantIdClaimType = "tenant_id";
    private const string SessionIdClaimType = "session_id";

    public static bool TryRead(ClaimsPrincipal user, out AuthenticatedIdentity identity)
    {
        identity = default;

        if (!TryGetExactlyOneCanonicalGuidClaim(user, SubClaimType, out var userId) ||
            !TryGetExactlyOneCanonicalGuidClaim(user, TenantIdClaimType, out var tenantId) ||
            !TryGetExactlyOneCanonicalGuidClaim(user, SessionIdClaimType, out var sessionId))
        {
            return false;
        }

        identity = new AuthenticatedIdentity(userId, tenantId, sessionId);
        return true;
    }

    /// <summary>
    /// True only when exactly one claim of <paramref name="claimType"/>
    /// exists and its value is a GUID in the exact canonical hyphenated form
    /// (<c>Guid.ToString()</c>'s default "D" format — what
    /// <c>JwtTokenGenerator</c> always emits). A duplicated claim or any other
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
}
