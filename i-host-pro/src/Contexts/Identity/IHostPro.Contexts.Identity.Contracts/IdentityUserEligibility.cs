namespace IHostPro.Contexts.Identity.Contracts;

/// <summary>
/// The minimal result of <see cref="IIdentityUserEligibilityReader.GetAsync"/>
/// (Fase 2, Incremento 1, Checkpoint 5 plan, item 3) — deliberately carries no
/// name, email, full role list, password hash, session or token data, only
/// what a caller needs to decide eligibility. A <c>null</c> instance (not
/// this record) represents "user does not exist in the given tenant,
/// including cross-tenant" — see the interface's own doc comment.
/// </summary>
public sealed record IdentityUserEligibility(Guid UserId, bool IsActive, bool HasRequiredRole);
