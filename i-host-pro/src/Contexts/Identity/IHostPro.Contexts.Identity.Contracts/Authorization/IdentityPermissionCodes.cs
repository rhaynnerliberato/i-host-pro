namespace IHostPro.Contexts.Identity.Contracts.Authorization;

/// <summary>
/// Single, framework-neutral source of truth for the platform's fixed
/// permission codes that something outside the seed itself also needs to
/// reference — both <c>IdentityCatalogSeed</c> (Infrastructure, seeds the
/// persisted catalog) and <c>IdentityAuthorizationExtensions</c>/
/// <c>PermissionRequirement</c> (Api, declare authorization policies)
/// reference these same constants, rather than each holding its own copy of
/// the literal string.
///
/// Declared in <c>Identity.Contracts</c> (moved here from
/// <c>Identity.Application</c> — Fase 2, Incremento 1, Checkpoint 1, approved
/// design) because another Bounded Context (Property Management) now also
/// needs to reference two of these codes by value
/// (<see cref="PropertiesManage"/>, <see cref="PropertiesReadOwnOwner"/>),
/// and "Architecture Principles.md" §14's sole synchronous-query exceptions
/// are Identity &amp; Access and Configuration &amp; Policy — Property
/// Management may reference <c>Identity.Contracts</c> only, never
/// <c>Identity.Application</c>/<c>Identity.Infrastructure</c>. Moving a type
/// between assemblies is not binary-compatible; accepted here because the
/// whole repository is compiled together, no external package consumes the
/// old assembly, and the persisted textual values are unchanged.
///
/// Only the codes actually consumed outside the catalog itself are listed
/// here — the full permission catalog (Documento 09 §12-15) remains
/// <c>IdentityCatalogSeed.Permissions</c>' sole responsibility; a code is
/// promoted to a constant here only when something other than the seed also
/// needs to reference it by value.
/// </summary>
public static class IdentityPermissionCodes
{
    public const string UsersManage = "USERS:MANAGE";
    public const string RolesRead = "ROLES:READ";
    public const string PermissionsRead = "PERMISSIONS:READ";
    public const string PropertiesManage = "PROPERTIES:MANAGE";
    public const string PropertiesReadOwnOwner = "PROPERTIES:READ:OWN_OWNER";
}
