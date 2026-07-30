namespace IHostPro.Contexts.Identity.Application.Authorization;

/// <summary>
/// Single, framework-neutral source of truth for the platform's fixed
/// permission codes that something outside the seed itself also needs to
/// reference — both <c>IdentityCatalogSeed</c> (Infrastructure, seeds the
/// persisted catalog) and <c>IdentityAuthorizationExtensions</c>/<c>PermissionRequirement</c>
/// (Api, declare authorization policies) reference these same constants,
/// rather than each holding its own copy of the literal string (Incremento 3
/// plan, Checkpoint 1 follow-up — approved consistency fix). Declared here,
/// in Application, so Infrastructure (which already depends on Application)
/// can reference it without Application ever depending on ASP.NET Core.
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
}
