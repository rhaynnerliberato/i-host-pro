using System.Security.Claims;

namespace IHostPro.Contexts.Configuration.Api.Http;

/// <summary>
/// Canonical, single-source claim extraction for every Policies controller
/// action — mirrors <c>Reservations.Api.Http.ReservationsIdentityReader</c>'s
/// own independent copy, limited to the two claims this context actually
/// needs. Re-validated here even though <c>[Authorize]</c> already proved
/// the token itself was valid: that only proves signature/lifetime/issuer/
/// audience — never that this action can blindly trust
/// <c>ClaimsPrincipal.Claims</c> for its own use.
/// </summary>
public static class ConfigurationIdentityReader
{
    private const string SubClaimType = "sub";
    private const string TenantIdClaimType = "tenant_id";

    public static bool TryRead(ClaimsPrincipal user, out AuthenticatedConfigurationIdentity identity)
    {
        identity = default;

        if (!TryGetExactlyOneCanonicalGuidClaim(user, SubClaimType, out var userId) ||
            !TryGetExactlyOneCanonicalGuidClaim(user, TenantIdClaimType, out var tenantId))
        {
            return false;
        }

        identity = new AuthenticatedConfigurationIdentity(userId, tenantId);
        return true;
    }

    private static bool TryGetExactlyOneCanonicalGuidClaim(ClaimsPrincipal principal, string claimType, out Guid value)
    {
        value = Guid.Empty;

        var matches = principal.Claims.Where(c => c.Type == claimType).ToArray();
        if (matches.Length != 1)
            return false;

        return Guid.TryParseExact(matches[0].Value, "D", out value);
    }
}
