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

    /// <summary>
    /// Fase 3, Incremento 1 plan, item 5. Already seeded into the persisted
    /// catalog since <c>IdentityCatalogSeed</c>'s original migration (ADMIN
    /// and OPERATOR — Documento 09 §5-§6); this constant is only now
    /// promoted here because Reservations' controller needs to reference it
    /// by value for the first time. No new migration required.
    /// </summary>
    public const string ReservationsManage = "RESERVATIONS:MANAGE";

    /// <summary>
    /// Fase 5, Incremento 1 (Policy Engine Foundation), official decisions §7.
    /// Already seeded into the persisted catalog since
    /// <c>IdentityCatalogSeed</c>'s original migration (ADMIN=manage,
    /// AI_AGENT=read — Documento 09 §5/§9); promoted here only now because
    /// Configuration &amp; Policy's controller needs to reference them by
    /// value for the first time. No new migration required.
    /// <c>SETTINGS:READ</c>/<c>SETTINGS:MANAGE</c> remain reserved for the
    /// future Configurações increment and are deliberately not promoted yet.
    /// </summary>
    public const string PoliciesRead = "POLICIES:READ";
    public const string PoliciesManage = "POLICIES:MANAGE";

    /// <summary>
    /// Fase 6, Incremento 1 (Housekeeping Foundation). Already seeded into
    /// the persisted catalog since <c>IdentityCatalogSeed</c>'s original
    /// migration (ADMIN=manage, OPERATOR=manage, HOUSEKEEPER=manage:own —
    /// Documento 09 §5-§7); promoted here only now because Housekeeping's
    /// controller/tests need to reference them by value for the first time.
    /// No new migration required. <c>CLEANINGS:READ</c>/<c>CLEANINGS:READ:OWN_OWNER</c>
    /// remain unpromoted — nothing outside the seed needs them by value yet.
    /// </summary>
    public const string CleaningsManage = "CLEANINGS:MANAGE";
    public const string CleaningsManageOwnCleaning = "CLEANINGS:MANAGE:OWN_CLEANING";

    /// <summary>
    /// Fase 7, Incremento 1 (Agenda Foundation). Already seeded into the
    /// persisted catalog since <c>IdentityCatalogSeed</c>'s original Fase 1
    /// migration (ADMIN=manage, OPERATOR=manage, HOUSEKEEPER=read,
    /// AI_AGENT=read — Documento 09 §5-§7); promoted here only now because
    /// <c>ScheduleController</c> needs to reference them by value for the
    /// first time. No new migration required. <c>SCHEDULE:READ:OWN_OWNER</c>
    /// remains unpromoted — Property Owner access is deliberately deferred
    /// this increment (no owner→property mapping available inside
    /// Reservation &amp; Scheduling yet — Checkpoint 0 gate).
    /// </summary>
    public const string ScheduleManage = "SCHEDULE:MANAGE";
    public const string ScheduleRead = "SCHEDULE:READ";

    /// <summary>
    /// Fase 7, Incremento 2 (Dashboard &amp; Reporting Foundation). Already
    /// seeded into the persisted catalog since <c>IdentityCatalogSeed</c>'s
    /// original Fase 1 migration (ADMIN=manage, OPERATOR=read, AI_AGENT=use —
    /// Documento 09 §5-§7); promoted here only now because
    /// <c>DashboardController</c> needs to reference them by value for the
    /// first time (Checkpoint 2). No new migration required.
    /// <c>DASHBOARD:READ:OWN_OWNER</c> remains unpromoted — Property Owner
    /// access is deliberately deferred this checkpoint (Checkpoint 2 scope
    /// gate).
    /// </summary>
    public const string DashboardManage = "DASHBOARD:MANAGE";
    public const string DashboardRead = "DASHBOARD:READ";
}
