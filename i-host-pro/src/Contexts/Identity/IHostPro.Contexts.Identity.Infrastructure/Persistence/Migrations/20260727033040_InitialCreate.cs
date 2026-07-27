using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace IHostPro.Contexts.Identity.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "identity");

            migrationBuilder.CreateTable(
                name: "permissions",
                schema: "identity",
                columns: table => new
                {
                    code = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    resource = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    action = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    scope = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_permissions", x => x.code);
                });

            migrationBuilder.CreateTable(
                name: "roles",
                schema: "identity",
                columns: table => new
                {
                    code = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    display_name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_roles", x => x.code);
                });

            migrationBuilder.CreateTable(
                name: "tenants",
                schema: "identity",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    slug = table.Column<string>(type: "character varying(63)", maxLength: 63, nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    status = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    activated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_tenants", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "role_permissions",
                schema: "identity",
                columns: table => new
                {
                    role_code = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    permission_code = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_role_permissions", x => new { x.role_code, x.permission_code });
                    table.ForeignKey(
                        name: "FK_role_permissions_permissions_permission_code",
                        column: x => x.permission_code,
                        principalSchema: "identity",
                        principalTable: "permissions",
                        principalColumn: "code",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_role_permissions_roles_role_code",
                        column: x => x.role_code,
                        principalSchema: "identity",
                        principalTable: "roles",
                        principalColumn: "code",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "users",
                schema: "identity",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    email = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false),
                    normalized_email = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false),
                    full_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    password_hash = table.Column<string>(type: "text", nullable: false),
                    security_stamp = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    status = table.Column<int>(type: "integer", nullable: false),
                    failed_access_count = table.Column<int>(type: "integer", nullable: false),
                    lockout_end = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_login_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_users", x => x.id);
                    table.UniqueConstraint("AK_users_tenant_id_id", x => new { x.tenant_id, x.id });
                    table.ForeignKey(
                        name: "FK_users_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalSchema: "identity",
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "sessions",
                schema: "identity",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    last_activity_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    device = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    browser = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    ip_address = table.Column<string>(type: "character varying(45)", maxLength: 45, nullable: true),
                    approximate_location = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    status = table.Column<int>(type: "integer", nullable: false),
                    revoked_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    revocation_reason = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_sessions", x => x.id);
                    table.UniqueConstraint("AK_sessions_tenant_id_id_user_id", x => new { x.tenant_id, x.id, x.user_id });
                    table.ForeignKey(
                        name: "FK_sessions_users_tenant_id_user_id",
                        columns: x => new { x.tenant_id, x.user_id },
                        principalSchema: "identity",
                        principalTable: "users",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "user_roles",
                schema: "identity",
                columns: table => new
                {
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    role_code = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    assigned_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    assigned_by_user_id = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_user_roles", x => new { x.user_id, x.role_code });
                    table.ForeignKey(
                        name: "FK_user_roles_roles_role_code",
                        column: x => x.role_code,
                        principalSchema: "identity",
                        principalTable: "roles",
                        principalColumn: "code",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_user_roles_users_tenant_id_assigned_by_user_id",
                        columns: x => new { x.tenant_id, x.assigned_by_user_id },
                        principalSchema: "identity",
                        principalTable: "users",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_user_roles_users_tenant_id_user_id",
                        columns: x => new { x.tenant_id, x.user_id },
                        principalSchema: "identity",
                        principalTable: "users",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "refresh_tokens",
                schema: "identity",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    token_id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    session_id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    token_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    issued_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    revoked_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    revocation_reason = table.Column<int>(type: "integer", nullable: true),
                    previous_token_id = table.Column<Guid>(type: "uuid", nullable: true),
                    replaced_by_token_id = table.Column<Guid>(type: "uuid", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_refresh_tokens", x => x.id);
                    table.ForeignKey(
                        name: "FK_refresh_tokens_sessions_tenant_id_session_id_user_id",
                        columns: x => new { x.tenant_id, x.session_id, x.user_id },
                        principalSchema: "identity",
                        principalTable: "sessions",
                        principalColumns: new[] { "tenant_id", "id", "user_id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.InsertData(
                schema: "identity",
                table: "permissions",
                columns: new[] { "code", "action", "resource", "scope" },
                values: new object[,]
                {
                    { "AUDIT:READ", "READ", "AUDIT", null },
                    { "AUDIT:USE", "USE", "AUDIT", null },
                    { "CLEANINGS:MANAGE", "MANAGE", "CLEANINGS", null },
                    { "CLEANINGS:MANAGE:OWN_CLEANING", "MANAGE", "CLEANINGS", "OWN_CLEANING" },
                    { "CLEANINGS:READ", "READ", "CLEANINGS", null },
                    { "CLEANINGS:READ:OWN_OWNER", "READ", "CLEANINGS", "OWN_OWNER" },
                    { "DASHBOARD:MANAGE", "MANAGE", "DASHBOARD", null },
                    { "DASHBOARD:READ", "READ", "DASHBOARD", null },
                    { "DASHBOARD:READ:OWN_OWNER", "READ", "DASHBOARD", "OWN_OWNER" },
                    { "DASHBOARD:USE", "USE", "DASHBOARD", null },
                    { "PERMISSIONS:READ", "READ", "PERMISSIONS", null },
                    { "POLICIES:MANAGE", "MANAGE", "POLICIES", null },
                    { "POLICIES:READ", "READ", "POLICIES", null },
                    { "PROPERTIES:MANAGE", "MANAGE", "PROPERTIES", null },
                    { "PROPERTIES:READ", "READ", "PROPERTIES", null },
                    { "PROPERTIES:READ:OWN_OWNER", "READ", "PROPERTIES", "OWN_OWNER" },
                    { "REPORTS:MANAGE", "MANAGE", "REPORTS", null },
                    { "REPORTS:READ", "READ", "REPORTS", null },
                    { "REPORTS:READ:OWN_OWNER", "READ", "REPORTS", "OWN_OWNER" },
                    { "REPORTS:USE", "USE", "REPORTS", null },
                    { "RESERVATIONS:MANAGE", "MANAGE", "RESERVATIONS", null },
                    { "RESERVATIONS:READ", "READ", "RESERVATIONS", null },
                    { "RESERVATIONS:READ:OWN_OWNER", "READ", "RESERVATIONS", "OWN_OWNER" },
                    { "ROLES:READ", "READ", "ROLES", null },
                    { "SCHEDULE:MANAGE", "MANAGE", "SCHEDULE", null },
                    { "SCHEDULE:READ", "READ", "SCHEDULE", null },
                    { "SCHEDULE:READ:OWN_OWNER", "READ", "SCHEDULE", "OWN_OWNER" },
                    { "SETTINGS:MANAGE", "MANAGE", "SETTINGS", null },
                    { "SETTINGS:READ", "READ", "SETTINGS", null },
                    { "TEMPLATES:MANAGE", "MANAGE", "TEMPLATES", null },
                    { "TEMPLATES:READ", "READ", "TEMPLATES", null },
                    { "USERS:MANAGE", "MANAGE", "USERS", null }
                });

            migrationBuilder.InsertData(
                schema: "identity",
                table: "roles",
                columns: new[] { "code", "display_name" },
                values: new object[,]
                {
                    { "ADMIN", "Administrador" },
                    { "AI_AGENT", "Agente IA" },
                    { "HOUSEKEEPER", "Faxineira" },
                    { "INTEGRATION", "Integração" },
                    { "OPERATOR", "Operador" },
                    { "PROPERTY_OWNER", "Proprietário" },
                    { "SYSTEM", "Sistema" }
                });

            migrationBuilder.InsertData(
                schema: "identity",
                table: "role_permissions",
                columns: new[] { "permission_code", "role_code" },
                values: new object[,]
                {
                    { "AUDIT:READ", "ADMIN" },
                    { "CLEANINGS:MANAGE", "ADMIN" },
                    { "DASHBOARD:MANAGE", "ADMIN" },
                    { "PERMISSIONS:READ", "ADMIN" },
                    { "POLICIES:MANAGE", "ADMIN" },
                    { "PROPERTIES:MANAGE", "ADMIN" },
                    { "REPORTS:MANAGE", "ADMIN" },
                    { "RESERVATIONS:MANAGE", "ADMIN" },
                    { "ROLES:READ", "ADMIN" },
                    { "SCHEDULE:MANAGE", "ADMIN" },
                    { "SETTINGS:MANAGE", "ADMIN" },
                    { "TEMPLATES:MANAGE", "ADMIN" },
                    { "USERS:MANAGE", "ADMIN" },
                    { "AUDIT:USE", "AI_AGENT" },
                    { "CLEANINGS:READ", "AI_AGENT" },
                    { "DASHBOARD:USE", "AI_AGENT" },
                    { "POLICIES:READ", "AI_AGENT" },
                    { "PROPERTIES:READ", "AI_AGENT" },
                    { "REPORTS:USE", "AI_AGENT" },
                    { "RESERVATIONS:READ", "AI_AGENT" },
                    { "SCHEDULE:READ", "AI_AGENT" },
                    { "SETTINGS:READ", "AI_AGENT" },
                    { "TEMPLATES:READ", "AI_AGENT" },
                    { "CLEANINGS:MANAGE:OWN_CLEANING", "HOUSEKEEPER" },
                    { "PROPERTIES:READ", "HOUSEKEEPER" },
                    { "SCHEDULE:READ", "HOUSEKEEPER" },
                    { "AUDIT:READ", "OPERATOR" },
                    { "CLEANINGS:MANAGE", "OPERATOR" },
                    { "DASHBOARD:READ", "OPERATOR" },
                    { "PROPERTIES:READ", "OPERATOR" },
                    { "REPORTS:READ", "OPERATOR" },
                    { "RESERVATIONS:MANAGE", "OPERATOR" },
                    { "SCHEDULE:MANAGE", "OPERATOR" },
                    { "CLEANINGS:READ:OWN_OWNER", "PROPERTY_OWNER" },
                    { "DASHBOARD:READ:OWN_OWNER", "PROPERTY_OWNER" },
                    { "PROPERTIES:READ:OWN_OWNER", "PROPERTY_OWNER" },
                    { "REPORTS:READ:OWN_OWNER", "PROPERTY_OWNER" },
                    { "RESERVATIONS:READ:OWN_OWNER", "PROPERTY_OWNER" },
                    { "SCHEDULE:READ:OWN_OWNER", "PROPERTY_OWNER" }
                });

            migrationBuilder.CreateIndex(
                name: "IX_refresh_tokens_tenant_id_session_id_user_id",
                schema: "identity",
                table: "refresh_tokens",
                columns: new[] { "tenant_id", "session_id", "user_id" });

            migrationBuilder.CreateIndex(
                name: "IX_refresh_tokens_tenant_id_user_id",
                schema: "identity",
                table: "refresh_tokens",
                columns: new[] { "tenant_id", "user_id" });

            migrationBuilder.CreateIndex(
                name: "IX_refresh_tokens_token_id",
                schema: "identity",
                table: "refresh_tokens",
                column: "token_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_role_permissions_permission_code",
                schema: "identity",
                table: "role_permissions",
                column: "permission_code");

            migrationBuilder.CreateIndex(
                name: "IX_sessions_tenant_id_user_id",
                schema: "identity",
                table: "sessions",
                columns: new[] { "tenant_id", "user_id" });

            migrationBuilder.CreateIndex(
                name: "IX_tenants_slug",
                schema: "identity",
                table: "tenants",
                column: "slug",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_user_roles_role_code",
                schema: "identity",
                table: "user_roles",
                column: "role_code");

            migrationBuilder.CreateIndex(
                name: "IX_user_roles_tenant_id_assigned_by_user_id",
                schema: "identity",
                table: "user_roles",
                columns: new[] { "tenant_id", "assigned_by_user_id" });

            migrationBuilder.CreateIndex(
                name: "IX_user_roles_tenant_id_user_id",
                schema: "identity",
                table: "user_roles",
                columns: new[] { "tenant_id", "user_id" });

            migrationBuilder.CreateIndex(
                name: "IX_users_tenant_id_normalized_email",
                schema: "identity",
                table: "users",
                columns: new[] { "tenant_id", "normalized_email" },
                unique: true);

            // --- Least-privilege grants (Incremento 1 plan, adendo final,
            // Section 7) ---
            //
            // Precondition: the ihostpro_app / ihostpro_migrator roles must
            // already exist in the target database before this migration
            // runs (provisioned by docker/postgres/init/01-create-roles.sh
            // for local development; an equivalent operational procedure is
            // required for homologation/production — see the Incremento 1
            // report). This migration NEVER creates, alters, or drops a
            // role — role lifecycle is exclusively the infrastructure
            // script's responsibility (adendo final review, Section 4).
            //
            // ihostpro_app (used by IHostPro.Api / IHostPro.Worker) receives
            // only CONNECT + schema USAGE + CRUD on tables — never CREATE,
            // ALTER, DROP, TRUNCATE, BYPASSRLS, or membership in
            // ihostpro_migrator. ihostpro_migrator (used exclusively by
            // IHostPro.MigrationRunner) owns the schema/tables by having
            // executed this migration, which is what lets it bypass RLS
            // without ever being granted BYPASSRLS explicitly.
            migrationBuilder.Sql("REVOKE ALL ON SCHEMA identity FROM PUBLIC;");
            migrationBuilder.Sql("GRANT USAGE ON SCHEMA identity TO ihostpro_app;");
            migrationBuilder.Sql("GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA identity TO ihostpro_app;");
            migrationBuilder.Sql(
                "ALTER DEFAULT PRIVILEGES FOR ROLE ihostpro_migrator IN SCHEMA identity " +
                "GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO ihostpro_app;");

            // --- Row-Level Security (Incremento 1 plan, adendo final,
            // Section 3/5) ---
            //
            // Only tenant-owned tables (users, user_roles, sessions,
            // refresh_tokens). `tenants`, `roles`, `permissions`,
            // `role_permissions` are platform-level catalogs and are
            // deliberately NOT protected by RLS.
            //
            // current_setting('app.tenant_id', true) returns NULL instead of
            // raising when unset; `tenant_id = NULL` is never true in SQL, so
            // the absence of a resolved tenant fails closed (zero rows
            // visible/writable) rather than erroring. FORCE ROW LEVEL
            // SECURITY applies the policy even to the table owner — cheap
            // defense-in-depth, since ihostpro_app is never the owner anyway.
            // WITH CHECK (not just USING) protects INSERT and the new values
            // of UPDATE, not only SELECT/DELETE visibility.
            foreach (var tenantOwnedTable in new[] { "users", "user_roles", "sessions", "refresh_tokens" })
            {
                migrationBuilder.Sql($"ALTER TABLE identity.{tenantOwnedTable} ENABLE ROW LEVEL SECURITY;");
                migrationBuilder.Sql($"ALTER TABLE identity.{tenantOwnedTable} FORCE ROW LEVEL SECURITY;");
                migrationBuilder.Sql($"""
                    CREATE POLICY tenant_isolation ON identity.{tenantOwnedTable}
                        USING (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid)
                        WITH CHECK (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid);
                    """);
            }
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Schema-level grant cleanup only — mirrors exactly what Up()
            // granted. Policies are not dropped explicitly: PostgreSQL drops
            // a table's policies automatically as part of DROP TABLE, so
            // there is nothing left to clean up once the tables below are
            // gone. The `identity` schema itself is deliberately left in
            // place (not dropped) — it is owned by ihostpro_migrator
            // regardless of this migration's lifecycle, and dropping a
            // shared schema from a single migration's Down() is broader than
            // "appropriate" here (adendo final review, Section 4). Nothing
            // below touches a role — role lifecycle belongs exclusively to
            // docker/postgres/init/01-create-roles.sh.
            migrationBuilder.Sql(
                "ALTER DEFAULT PRIVILEGES FOR ROLE ihostpro_migrator IN SCHEMA identity " +
                "REVOKE SELECT, INSERT, UPDATE, DELETE ON TABLES FROM ihostpro_app;");
            migrationBuilder.Sql("REVOKE ALL ON ALL TABLES IN SCHEMA identity FROM ihostpro_app;");
            migrationBuilder.Sql("REVOKE USAGE ON SCHEMA identity FROM ihostpro_app;");

            migrationBuilder.DropTable(
                name: "refresh_tokens",
                schema: "identity");

            migrationBuilder.DropTable(
                name: "role_permissions",
                schema: "identity");

            migrationBuilder.DropTable(
                name: "user_roles",
                schema: "identity");

            migrationBuilder.DropTable(
                name: "sessions",
                schema: "identity");

            migrationBuilder.DropTable(
                name: "permissions",
                schema: "identity");

            migrationBuilder.DropTable(
                name: "roles",
                schema: "identity");

            migrationBuilder.DropTable(
                name: "users",
                schema: "identity");

            migrationBuilder.DropTable(
                name: "tenants",
                schema: "identity");
        }
    }
}
