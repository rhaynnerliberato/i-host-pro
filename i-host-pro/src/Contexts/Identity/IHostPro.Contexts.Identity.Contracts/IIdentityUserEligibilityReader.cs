namespace IHostPro.Contexts.Identity.Contracts;

/// <summary>
/// The one synchronous, cross-context query Property Management's Ownership
/// flow is authorized to make against Identity &amp; Access (Fase 2,
/// Incremento 1, Checkpoint 5 plan, item 3) — "Architecture Principles.md"
/// §14's synchronous-query exception. Declared here, in
/// <c>Identity.Contracts</c> (never <c>Identity.Application</c>/
/// <c>Identity.Infrastructure</c>/<c>Identity.Api</c>), so Property
/// Management's Application layer can depend on the contract alone, exactly
/// like it already does for <c>IdentityPermissionCodes</c>. Implemented
/// exclusively in <c>Identity.Infrastructure</c> (item 5) — never callable
/// from outside a Bounded Context that references only this assembly.
/// </summary>
public interface IIdentityUserEligibilityReader
{
    /// <summary>
    /// Returns <c>null</c> when no user with <paramref name="userId"/> exists
    /// within <paramref name="tenantId"/> — including when the user exists
    /// but belongs to a DIFFERENT tenant, indistinguishable by design, same
    /// convention as every other cross-tenant lookup in this codebase. A
    /// non-null result's <see cref="IdentityUserEligibility.IsActive"/> is
    /// <c>false</c> when the user exists but is blocked;
    /// <see cref="IdentityUserEligibility.HasRequiredRole"/> is <c>false</c>
    /// when the user exists but does not currently hold
    /// <paramref name="requiredRoleCode"/>. Read-only: never mutates state,
    /// writes an audit entry, or publishes an event.
    /// </summary>
    Task<IdentityUserEligibility?> GetAsync(
        Guid tenantId,
        Guid userId,
        string requiredRoleCode,
        CancellationToken cancellationToken);
}
