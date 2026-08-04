using System.Security.Claims;

namespace IHostPro.Contexts.PropertyManagement.Api.Http;

/// <summary>
/// Canonical, single-source claim extraction for every Property Management
/// controller action (Checkpoint 2 plan, item 3). No genuine, reusable
/// abstraction for this exists outside <c>Identity.Api</c> today — Identity's
/// own <c>AuthenticatedIdentityReader</c> lives in
/// <c>IHostPro.Contexts.Identity.Api.Http</c>, which this project must never
/// reference (Checkpoint 2 plan, item 3: "não faça Property Management
/// depender de Identity.Api") — so this is a minimal, independent copy,
/// limited to the two claims this context actually needs
/// (<see cref="AuthenticatedPropertyManagementIdentity.TenantId"/>/
/// <see cref="AuthenticatedPropertyManagementIdentity.UserId"/>), following
/// exactly the same claim types <c>JwtTokenGenerator</c>/
/// <c>ConfigureJwtBearerOptions</c> already emit/validate (<c>sub</c>,
/// <c>tenant_id</c>).
///
/// Re-validated here even though <c>[Authorize]</c> already proved the token
/// itself was valid: that only proves signature/lifetime/issuer/audience —
/// never that this action can blindly trust <c>ClaimsPrincipal.Claims</c> for
/// its own use.
/// </summary>
public static class PropertyManagementIdentityReader
{
    private const string SubClaimType = "sub";
    private const string TenantIdClaimType = "tenant_id";

    public static bool TryRead(ClaimsPrincipal user, out AuthenticatedPropertyManagementIdentity identity)
    {
        identity = default;

        if (!TryGetExactlyOneCanonicalGuidClaim(user, SubClaimType, out var userId) ||
            !TryGetExactlyOneCanonicalGuidClaim(user, TenantIdClaimType, out var tenantId))
        {
            return false;
        }

        identity = new AuthenticatedPropertyManagementIdentity(userId, tenantId);
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
