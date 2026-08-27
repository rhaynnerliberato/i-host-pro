using IHostPro.Contexts.Identity.Contracts.Authorization;
using IHostPro.Contexts.Identity.Domain;

namespace IHostPro.Contexts.Identity.Infrastructure.Seed;

/// <summary>
/// The platform's fixed Role/Permission/RolePermission catalog, seeded via EF
/// Core migration data (deterministic, idempotent-by-migration — applied
/// exactly once when the migration runs). Every entry below is traceable to
/// Documento 09: role codes to §4-§11, permission codes to the "Matriz
/// Simplificada" (§15) plus §12 for the Identity-owned resources
/// (USERS/ROLES/PERMISSIONS). Two deliberate overrides of the §15 matrix are
/// applied where a role's explicit "Não poderá" narrative directly
/// contradicts a matrix cell (Incremento 1 plan, adendo final, Section 10):
///
/// <list type="bullet">
/// <item>AUDIT: Admin's matrix cell is X (full control), but §5 explicitly
/// says Admin cannot alter/delete audit records — seeded as READ, not
/// MANAGE.</item>
/// <item>SCHEDULE: Faxineira's matrix cell is X, but §7 explicitly says she
/// cannot edit the agenda — seeded as READ, not MANAGE.</item>
/// </list>
///
/// <c>ROLES:MANAGE</c> and <c>PERMISSIONS:MANAGE</c> are deliberately NOT
/// seeded — no documented rule authorizes editing the role/permission catalog
/// itself in this phase (Documento 09 §18 implies the catalog is
/// platform-fixed for v1). <c>SYSTEM</c> and <c>INTEGRATION</c> roles remain
/// seeded with zero permissions — Documento 09 §10-§11 describe them as the
/// SYSTEM-TO-SYSTEM actor identity for background jobs/external connectors,
/// never a human administrator; that narrative capability still depends on
/// Bounded Contexts (Platform, and the runtime activation of External
/// Integrations) not yet built. <c>INTEGRATIONS:MANAGE</c> below is a
/// different concept entirely — the human ADMIN capability to configure an
/// integration's non-secret settings via an administrative API (Fase 9,
/// Checkpoint 2.1) — and is, exceptionally, a genuinely new catalog entry
/// approved explicitly for this checkpoint (CP2.0 audit + CP2.1 mandate,
/// Decisão K), not a promotion of an already-seeded-but-unused code like
/// every other constant in <c>IdentityPermissionCodes</c>.
///
/// <c>GUEST_OPERATIONS:MANAGE</c>/<c>GUEST_OPERATIONS:READ</c> (Fase 10,
/// Checkpoint 1 — Guest Operations Foundation) are the SECOND genuinely new
/// catalog entries, explicitly authorized for this checkpoint. Unlike
/// <c>INTEGRATIONS:MANAGE</c>, no real controller exists yet to consume
/// them (CP1 has zero public API endpoints) — they follow the
/// SETTINGS:MANAGE/SETTINGS:READ lifecycle instead: seeded here (and granted
/// to ADMIN below) ahead of any consumer, deliberately NOT promoted to
/// <c>IdentityPermissionCodes</c> and NOT registered in
/// <c>IdentityAuthorizationExtensions</c> until a future checkpoint's real
/// endpoint needs them.
/// </summary>
public static class IdentityCatalogSeed
{
    public static IReadOnlyList<Role> Roles { get; } =
    [
        new Role("ADMIN", "Administrador"),
        new Role("OPERATOR", "Operador"),
        new Role(IdentityRoleCodes.Housekeeper, "Faxineira"),
        new Role(IdentityRoleCodes.PropertyOwner, "Proprietário"),
        new Role("SYSTEM", "Sistema"),
        new Role("AI_AGENT", "Agente IA"),
        new Role("INTEGRATION", "Integração"),
    ];

    public static IReadOnlyList<Permission> Permissions { get; } =
    [
        new Permission("PROPERTIES:MANAGE", "PROPERTIES", "MANAGE"),
        new Permission("PROPERTIES:READ", "PROPERTIES", "READ"),
        new Permission("PROPERTIES:READ:OWN_OWNER", "PROPERTIES", "READ", "OWN_OWNER"),

        new Permission("RESERVATIONS:MANAGE", "RESERVATIONS", "MANAGE"),
        new Permission("RESERVATIONS:READ", "RESERVATIONS", "READ"),
        new Permission("RESERVATIONS:READ:OWN_OWNER", "RESERVATIONS", "READ", "OWN_OWNER"),

        new Permission("SCHEDULE:MANAGE", "SCHEDULE", "MANAGE"),
        new Permission("SCHEDULE:READ", "SCHEDULE", "READ"),
        new Permission("SCHEDULE:READ:OWN_OWNER", "SCHEDULE", "READ", "OWN_OWNER"),

        new Permission("CLEANINGS:MANAGE", "CLEANINGS", "MANAGE"),
        new Permission("CLEANINGS:MANAGE:OWN_CLEANING", "CLEANINGS", "MANAGE", "OWN_CLEANING"),
        new Permission("CLEANINGS:READ", "CLEANINGS", "READ"),
        new Permission("CLEANINGS:READ:OWN_OWNER", "CLEANINGS", "READ", "OWN_OWNER"),

        new Permission("POLICIES:MANAGE", "POLICIES", "MANAGE"),
        new Permission("POLICIES:READ", "POLICIES", "READ"),

        new Permission("TEMPLATES:MANAGE", "TEMPLATES", "MANAGE"),
        new Permission("TEMPLATES:READ", "TEMPLATES", "READ"),

        new Permission("SETTINGS:MANAGE", "SETTINGS", "MANAGE"),
        new Permission("SETTINGS:READ", "SETTINGS", "READ"),

        new Permission("AUDIT:READ", "AUDIT", "READ"),
        new Permission("AUDIT:USE", "AUDIT", "USE"),

        new Permission("DASHBOARD:MANAGE", "DASHBOARD", "MANAGE"),
        new Permission("DASHBOARD:READ", "DASHBOARD", "READ"),
        new Permission("DASHBOARD:READ:OWN_OWNER", "DASHBOARD", "READ", "OWN_OWNER"),
        new Permission("DASHBOARD:USE", "DASHBOARD", "USE"),

        new Permission("REPORTS:MANAGE", "REPORTS", "MANAGE"),
        new Permission("REPORTS:READ", "REPORTS", "READ"),
        new Permission("REPORTS:READ:OWN_OWNER", "REPORTS", "READ", "OWN_OWNER"),
        new Permission("REPORTS:USE", "REPORTS", "USE"),

        new Permission(IdentityPermissionCodes.UsersManage, "USERS", "MANAGE"),
        new Permission(IdentityPermissionCodes.RolesRead, "ROLES", "READ"),
        new Permission(IdentityPermissionCodes.PermissionsRead, "PERMISSIONS", "READ"),

        // Fase 9, Checkpoint 2.1 — CP2.1 mandate §24: ADMIN only, no
        // INTEGRATIONS:READ counterpart created by symmetry.
        new Permission(IdentityPermissionCodes.IntegrationsManage, "INTEGRATIONS", "MANAGE"),

        // Fase 10, Checkpoint 1 (Guest Operations Foundation) — CP1 has zero
        // public API endpoints, so, unlike IntegrationsManage above, these
        // are NOT promoted to IdentityPermissionCodes and have NO AddPolicy
        // registration in IdentityAuthorizationExtensions yet (that class's
        // own documented rule: only policies an existing endpoint actually
        // consumes are registered). Mirrors this same list's own
        // SETTINGS:MANAGE/SETTINGS:READ precedent — seeded ahead of any
        // consumer, promoted/wired only once a real controller references
        // them by value.
        new Permission("GUEST_OPERATIONS:MANAGE", "GUEST_OPERATIONS", "MANAGE"),
        new Permission("GUEST_OPERATIONS:READ", "GUEST_OPERATIONS", "READ"),
    ];

    public static IReadOnlyList<RolePermission> RolePermissions { get; } =
    [
        // ADMIN — Documento 09 §5.
        new RolePermission("ADMIN", "PROPERTIES:MANAGE"),
        new RolePermission("ADMIN", "RESERVATIONS:MANAGE"),
        new RolePermission("ADMIN", "SCHEDULE:MANAGE"),
        new RolePermission("ADMIN", "CLEANINGS:MANAGE"),
        new RolePermission("ADMIN", "POLICIES:MANAGE"),
        new RolePermission("ADMIN", "TEMPLATES:MANAGE"),
        new RolePermission("ADMIN", "SETTINGS:MANAGE"),
        new RolePermission("ADMIN", "AUDIT:READ"), // override — §5 "Não poderá: Alterar/Excluir auditoria"
        new RolePermission("ADMIN", "DASHBOARD:MANAGE"),
        new RolePermission("ADMIN", "REPORTS:MANAGE"),
        new RolePermission("ADMIN", IdentityPermissionCodes.UsersManage),
        new RolePermission("ADMIN", IdentityPermissionCodes.RolesRead),
        new RolePermission("ADMIN", IdentityPermissionCodes.PermissionsRead),
        new RolePermission("ADMIN", IdentityPermissionCodes.IntegrationsManage),
        new RolePermission("ADMIN", "GUEST_OPERATIONS:MANAGE"),
        new RolePermission("ADMIN", "GUEST_OPERATIONS:READ"),

        // OPERATOR — Documento 09 §6.
        new RolePermission("OPERATOR", "PROPERTIES:READ"),
        new RolePermission("OPERATOR", "RESERVATIONS:MANAGE"),
        new RolePermission("OPERATOR", "SCHEDULE:MANAGE"),
        new RolePermission("OPERATOR", "CLEANINGS:MANAGE"),
        new RolePermission("OPERATOR", "AUDIT:READ"),
        new RolePermission("OPERATOR", "DASHBOARD:READ"),
        new RolePermission("OPERATOR", "REPORTS:READ"),

        // HOUSEKEEPER — Documento 09 §7.
        new RolePermission(IdentityRoleCodes.Housekeeper,"PROPERTIES:READ"),
        new RolePermission(IdentityRoleCodes.Housekeeper,"SCHEDULE:READ"), // override — §7 "Não poderá: Editar agenda"
        new RolePermission(IdentityRoleCodes.Housekeeper,"CLEANINGS:MANAGE:OWN_CLEANING"),

        // PROPERTY_OWNER — Documento 09 §8.
        new RolePermission(IdentityRoleCodes.PropertyOwner, "PROPERTIES:READ:OWN_OWNER"),
        new RolePermission(IdentityRoleCodes.PropertyOwner, "RESERVATIONS:READ:OWN_OWNER"),
        new RolePermission(IdentityRoleCodes.PropertyOwner, "SCHEDULE:READ:OWN_OWNER"),
        new RolePermission(IdentityRoleCodes.PropertyOwner, "CLEANINGS:READ:OWN_OWNER"),
        new RolePermission(IdentityRoleCodes.PropertyOwner, "DASHBOARD:READ:OWN_OWNER"),
        new RolePermission(IdentityRoleCodes.PropertyOwner, "REPORTS:READ:OWN_OWNER"),

        // AI_AGENT — Documento 09 §9.
        new RolePermission("AI_AGENT", "PROPERTIES:READ"),
        new RolePermission("AI_AGENT", "RESERVATIONS:READ"),
        new RolePermission("AI_AGENT", "SCHEDULE:READ"),
        new RolePermission("AI_AGENT", "CLEANINGS:READ"),
        new RolePermission("AI_AGENT", "POLICIES:READ"),
        new RolePermission("AI_AGENT", "TEMPLATES:READ"),
        new RolePermission("AI_AGENT", "SETTINGS:READ"),
        new RolePermission("AI_AGENT", "AUDIT:USE"),
        new RolePermission("AI_AGENT", "DASHBOARD:USE"),
        new RolePermission("AI_AGENT", "REPORTS:USE"),

        // SYSTEM, INTEGRATION — no permission seeded (Documento 09 §10-§11 are
        // not represented in the §15 matrix; deliberately excluded, not an
        // oversight).
    ];
}
