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
/// platform-fixed for v1). <c>SYSTEM</c> and <c>INTEGRATION</c> roles are
/// seeded with zero permissions — they are not represented in the §15 matrix,
/// and their narrative capabilities (§10-§11) depend on Bounded Contexts
/// (Platform, External Integrations) that do not exist yet.
/// </summary>
public static class IdentityCatalogSeed
{
    public static IReadOnlyList<Role> Roles { get; } =
    [
        new Role("ADMIN", "Administrador"),
        new Role("OPERATOR", "Operador"),
        new Role("HOUSEKEEPER", "Faxineira"),
        new Role("PROPERTY_OWNER", "Proprietário"),
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

        new Permission("USERS:MANAGE", "USERS", "MANAGE"),
        new Permission("ROLES:READ", "ROLES", "READ"),
        new Permission("PERMISSIONS:READ", "PERMISSIONS", "READ"),
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
        new RolePermission("ADMIN", "USERS:MANAGE"),
        new RolePermission("ADMIN", "ROLES:READ"),
        new RolePermission("ADMIN", "PERMISSIONS:READ"),

        // OPERATOR — Documento 09 §6.
        new RolePermission("OPERATOR", "PROPERTIES:READ"),
        new RolePermission("OPERATOR", "RESERVATIONS:MANAGE"),
        new RolePermission("OPERATOR", "SCHEDULE:MANAGE"),
        new RolePermission("OPERATOR", "CLEANINGS:MANAGE"),
        new RolePermission("OPERATOR", "AUDIT:READ"),
        new RolePermission("OPERATOR", "DASHBOARD:READ"),
        new RolePermission("OPERATOR", "REPORTS:READ"),

        // HOUSEKEEPER — Documento 09 §7.
        new RolePermission("HOUSEKEEPER", "PROPERTIES:READ"),
        new RolePermission("HOUSEKEEPER", "SCHEDULE:READ"), // override — §7 "Não poderá: Editar agenda"
        new RolePermission("HOUSEKEEPER", "CLEANINGS:MANAGE:OWN_CLEANING"),

        // PROPERTY_OWNER — Documento 09 §8.
        new RolePermission("PROPERTY_OWNER", "PROPERTIES:READ:OWN_OWNER"),
        new RolePermission("PROPERTY_OWNER", "RESERVATIONS:READ:OWN_OWNER"),
        new RolePermission("PROPERTY_OWNER", "SCHEDULE:READ:OWN_OWNER"),
        new RolePermission("PROPERTY_OWNER", "CLEANINGS:READ:OWN_OWNER"),
        new RolePermission("PROPERTY_OWNER", "DASHBOARD:READ:OWN_OWNER"),
        new RolePermission("PROPERTY_OWNER", "REPORTS:READ:OWN_OWNER"),

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
