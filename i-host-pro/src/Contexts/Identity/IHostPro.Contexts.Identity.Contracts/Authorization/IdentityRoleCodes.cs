namespace IHostPro.Contexts.Identity.Contracts.Authorization;

/// <summary>
/// Single, framework-neutral source of truth for the platform's fixed role
/// codes that something outside the seed itself also needs to reference —
/// mirrors <see cref="IdentityPermissionCodes"/>'s own convention and
/// rationale exactly (Fase 2, Incremento 1, Checkpoint 5 plan, item 4). Only
/// the code actually consumed outside the catalog itself is listed here —
/// the full role catalog (Documento 09) remains
/// <c>IdentityCatalogSeed.Roles</c>'s sole responsibility; a code is
/// promoted to a constant only when something other than the seed also needs
/// to reference it by value. <see cref="PropertyOwner"/> is the first such
/// case: Property Management's Ownership eligibility check
/// (<c>IIdentityUserEligibilityReader</c>) needs to pass the exact role code
/// it requires, and <c>IdentityCatalogSeed</c> needs the same constant
/// rather than a second, independently-typed literal.
/// </summary>
public static class IdentityRoleCodes
{
    public const string PropertyOwner = "PROPERTY_OWNER";
}
