using System.Security.Claims;

namespace IHostPro.Contexts.Reservations.Api.Http;

/// <summary>
/// Canonical, single-source claim extraction for every Reservations
/// controller action — no genuine, reusable abstraction for this exists
/// outside <c>Identity.Api</c>, which this project must never reference
/// (mirrors <c>PropertyManagement.Api.Http.PropertyManagementIdentityReader</c>'s
/// own independent copy, limited to the two claims this context actually
/// needs).
///
/// Re-validated here even though <c>[Authorize]</c> already proved the token
/// itself was valid: that only proves signature/lifetime/issuer/audience —
/// never that this action can blindly trust <c>ClaimsPrincipal.Claims</c> for
/// its own use.
/// </summary>
public static class ReservationsIdentityReader
{
    private const string SubClaimType = "sub";
    private const string TenantIdClaimType = "tenant_id";

    public static bool TryRead(ClaimsPrincipal user, out AuthenticatedReservationsIdentity identity)
    {
        identity = default;

        if (!TryGetExactlyOneCanonicalGuidClaim(user, SubClaimType, out var userId) ||
            !TryGetExactlyOneCanonicalGuidClaim(user, TenantIdClaimType, out var tenantId))
        {
            return false;
        }

        identity = new AuthenticatedReservationsIdentity(userId, tenantId);
        return true;
    }

    /// <summary>
    /// True only when exactly one claim of <paramref name="claimType"/>
    /// exists and its value is a GUID in the exact canonical hyphenated form
    /// (<c>Guid.ToString()</c>'s default "D" format). A duplicated claim or
    /// any other GUID-like variant is rejected, never silently resolved via
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
