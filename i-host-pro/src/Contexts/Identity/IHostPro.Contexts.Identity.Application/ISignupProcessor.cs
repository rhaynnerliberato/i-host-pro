using IHostPro.BuildingBlocks.Domain;

namespace IHostPro.Contexts.Identity.Application;

/// <summary>
/// Self-service tenant + first-admin signup (Self-Service Identity &amp;
/// Onboarding Foundation gate). Implemented in Infrastructure (needs
/// <c>ITenantContext</c>) and called DIRECTLY by the Api controller,
/// bypassing <see cref="IIdentityRequestDispatcher"/> — unlike login/refresh,
/// there is no EXISTING tenant to resolve; this use case creates a brand new
/// one, so <c>IBootstrapRequest</c>/<c>TenantBootstrapBehavior</c> (reserved
/// for resolving an existing tenant from an authenticated-adjacent claim)
/// does not apply either.
///
/// Reuses the exact same domain factories <c>TenantProvisioning</c> (the
/// operator CLI) already uses (<c>Tenant.Provision</c>, <c>User.Register</c>,
/// the real Argon2id hasher, ADMIN role assignment) — but deliberately never
/// reuses that tool's "existing slug -&gt; add another admin to that tenant"
/// branch, which would let a stranger join an existing tenant by guessing its
/// name. A slug collision here always generates a different candidate slug
/// instead.
/// </summary>
public interface ISignupProcessor
{
    Task<Result<SignupResult>> ProcessAsync(
        string companyName, string adminFullName, string adminEmail, string password, CancellationToken cancellationToken);
}

/// <summary>The generated tenant slug (so the caller knows which "empresa" to log into later) plus a normal issued session, identical in shape to a successful login.</summary>
public sealed record SignupResult(string TenantSlug, AuthTokensResult Tokens);
