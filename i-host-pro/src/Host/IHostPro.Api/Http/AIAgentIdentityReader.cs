using System.Security.Claims;

namespace IHostPro.Api.Http;

/// <summary>
/// Canonical, single-source claim extraction for the AI Agent management
/// endpoints hosted directly in this process (Fase 11, Checkpoint 6 — no
/// separate AIAgent.Api project exists) — mirrors
/// <c>GuestOperations.Api.Http.GuestOperationsIdentityReader</c>'s own
/// independent copy, limited to the two claims these endpoints actually
/// need.
///
/// Re-validated here even though <c>[Authorize]</c> already proved the token
/// itself was valid: that only proves signature/lifetime/issuer/audience —
/// never that this action can blindly trust <c>ClaimsPrincipal.Claims</c> for
/// its own use.
/// </summary>
public static class AIAgentIdentityReader
{
    private const string SubClaimType = "sub";
    private const string TenantIdClaimType = "tenant_id";

    public static bool TryRead(ClaimsPrincipal user, out AuthenticatedAIAgentIdentity identity)
    {
        identity = default;

        if (!TryGetExactlyOneCanonicalGuidClaim(user, SubClaimType, out var userId) ||
            !TryGetExactlyOneCanonicalGuidClaim(user, TenantIdClaimType, out var tenantId))
        {
            return false;
        }

        identity = new AuthenticatedAIAgentIdentity(userId, tenantId);
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

public readonly record struct AuthenticatedAIAgentIdentity(Guid ActorId, Guid TenantId);
