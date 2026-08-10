using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace IHostPro.Contexts.Configuration.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "configuration");

            migrationBuilder.CreateTable(
                name: "policy_audit_log",
                schema: "configuration",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    policy_code = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    scope_type = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    scope_reference_id = table.Column<Guid>(type: "uuid", nullable: true),
                    previous_version = table.Column<int>(type: "integer", nullable: true),
                    new_version = table.Column<int>(type: "integer", nullable: false),
                    previous_value = table.Column<string>(type: "jsonb", nullable: true),
                    new_value = table.Column<string>(type: "jsonb", nullable: false),
                    author_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    occurred_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    origin = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    session_id = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    ip_address = table.Column<string>(type: "character varying(45)", maxLength: 45, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_policy_audit_log", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "policy_definitions",
                schema: "configuration",
                columns: table => new
                {
                    code = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    name = table.Column<string>(type: "character varying(150)", maxLength: 150, nullable: false),
                    description = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    category = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    value_type = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    schema_version = table.Column<int>(type: "integer", nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_policy_definitions", x => x.code);
                });

            migrationBuilder.CreateTable(
                name: "global_policy_values",
                schema: "configuration",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    policy_code = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    value = table.Column<string>(type: "jsonb", nullable: false),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_global_policy_values", x => x.id);
                    table.ForeignKey(
                        name: "FK_global_policy_values_policy_definitions_policy_code",
                        column: x => x.policy_code,
                        principalSchema: "configuration",
                        principalTable: "policy_definitions",
                        principalColumn: "code",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "policy_values",
                schema: "configuration",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    policy_code = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    scope_type = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    scope_reference_id = table.Column<Guid>(type: "uuid", nullable: true),
                    version = table.Column<int>(type: "integer", nullable: false),
                    value = table.Column<string>(type: "jsonb", nullable: false),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    is_current = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_policy_values", x => x.id);
                    table.ForeignKey(
                        name: "FK_policy_values_policy_definitions_policy_code",
                        column: x => x.policy_code,
                        principalSchema: "configuration",
                        principalTable: "policy_definitions",
                        principalColumn: "code",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.InsertData(
                schema: "configuration",
                table: "policy_definitions",
                columns: new[] { "code", "category", "description", "is_active", "name", "schema_version", "value_type" },
                values: new object[,]
                {
                    { "EARLY_CHECKIN", "CHECK_IN_OUT", "Regras operacionais para autorizar a entrada do hóspede antes do horário padrão de check-in.", true, "Early Check-in", 1, "Object" },
                    { "LATE_CHECKOUT", "CHECK_IN_OUT", "Regras operacionais para autorizar a saída do hóspede após o horário padrão de checkout, incluindo cobrança quando aplicável.", true, "Late Checkout", 1, "Object" }
                });

            migrationBuilder.CreateIndex(
                name: "IX_global_policy_values_policy_code",
                schema: "configuration",
                table: "global_policy_values",
                column: "policy_code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_policy_audit_log_tenant_id_occurred_at_utc",
                schema: "configuration",
                table: "policy_audit_log",
                columns: new[] { "tenant_id", "occurred_at_utc" });

            migrationBuilder.CreateIndex(
                name: "IX_policy_audit_log_tenant_id_policy_code_scope_type_scope_ref~",
                schema: "configuration",
                table: "policy_audit_log",
                columns: new[] { "tenant_id", "policy_code", "scope_type", "scope_reference_id", "occurred_at_utc" });

            migrationBuilder.CreateIndex(
                name: "IX_policy_values_policy_code",
                schema: "configuration",
                table: "policy_values",
                column: "policy_code");

            migrationBuilder.CreateIndex(
                name: "IX_policy_values_tenant_id_policy_code_scope_type_scope_refer~1",
                schema: "configuration",
                table: "policy_values",
                columns: new[] { "tenant_id", "policy_code", "scope_type", "scope_reference_id", "version" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_policy_values_tenant_id_policy_code_scope_type_scope_refere~",
                schema: "configuration",
                table: "policy_values",
                columns: new[] { "tenant_id", "policy_code", "scope_type", "scope_reference_id", "is_current" });

            // Only one row may be the current version for a given scope.
            // COALESCE is required because Postgres unique indexes treat
            // NULL as distinct from NULL — without it, two different
            // TENANT-scoped rows (whose scope_reference_id is always NULL)
            // would never conflict, defeating the constraint precisely for
            // the scope that needs it. Raw SQL because a COALESCE
            // expression index is not expressible through the EF Core
            // fluent index API.
            migrationBuilder.Sql("""
                CREATE UNIQUE INDEX "IX_policy_values_one_current_per_scope"
                ON configuration.policy_values (tenant_id, policy_code, scope_type, COALESCE(scope_reference_id, '00000000-0000-0000-0000-000000000000'::uuid))
                WHERE is_current;
                """);

            // --- Least-privilege grants (Fase 5, Incremento 1) ---
            //
            // Mirrors Reservations'/Property Management's/Identity's
            // InitialCreate migration exactly: ihostpro_app receives only
            // CONNECT + schema USAGE + the minimum CRUD each table actually
            // needs — never CREATE/ALTER/DROP/TRUNCATE/BYPASSRLS.
            //
            // policy_definitions/global_policy_values: platform catalog,
            // read-only for ihostpro_app — no tenant-facing command in this
            // increment ever writes to either.
            //
            // policy_values: append-only — ihostpro_app gets SELECT/INSERT,
            // plus UPDATE restricted to the single is_current column (the
            // only mutation a superseded version ever undergoes), never a
            // blanket UPDATE that could let application code alter Value/
            // Reason/etc. of an existing version (official decisions §4:
            // "Nunca sobrescrever destrutivamente uma versão anterior").
            //
            // policy_audit_log: append-only exactly like
            // reservation_audit_log — SELECT/INSERT only, never UPDATE/DELETE.
            migrationBuilder.Sql("REVOKE ALL ON SCHEMA configuration FROM PUBLIC;");
            migrationBuilder.Sql("GRANT USAGE ON SCHEMA configuration TO ihostpro_app;");

            migrationBuilder.Sql("GRANT SELECT ON configuration.policy_definitions TO ihostpro_app;");
            migrationBuilder.Sql("GRANT SELECT ON configuration.global_policy_values TO ihostpro_app;");
            migrationBuilder.Sql("GRANT SELECT, INSERT ON configuration.policy_values TO ihostpro_app;");
            migrationBuilder.Sql("GRANT UPDATE (is_current) ON configuration.policy_values TO ihostpro_app;");
            migrationBuilder.Sql("GRANT SELECT, INSERT ON configuration.policy_audit_log TO ihostpro_app;");

            migrationBuilder.Sql(
                "ALTER DEFAULT PRIVILEGES FOR ROLE ihostpro_migrator IN SCHEMA configuration " +
                "GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO ihostpro_app;");

            // --- Row-Level Security (Fase 5, Incremento 1) ---
            //
            // Only the two tenant-owned tables get RLS. policy_definitions
            // (platform catalog) and global_policy_values (official
            // decisions §4: GLOBAL values must never share the tenant-aware
            // RLS-protected table) carry no tenant_id and are deliberately
            // never subject to a tenant_isolation policy. Same
            // current_setting(..., true)/NULLIF fail-closed pattern as every
            // other Bounded Context: absence of a resolved tenant yields
            // zero rows visible/writable, never an error. FORCE applies the
            // policy even to the table owner.
            foreach (var tenantOwnedTable in new[] { "policy_values", "policy_audit_log" })
            {
                migrationBuilder.Sql($"ALTER TABLE configuration.{tenantOwnedTable} ENABLE ROW LEVEL SECURITY;");
                migrationBuilder.Sql($"ALTER TABLE configuration.{tenantOwnedTable} FORCE ROW LEVEL SECURITY;");
                migrationBuilder.Sql($"""
                    CREATE POLICY tenant_isolation ON configuration.{tenantOwnedTable}
                        USING (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid)
                        WITH CHECK (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid);
                    """);
            }
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Schema-level grant cleanup only — mirrors Reservations'/
            // Property Management's/Identity's InitialCreate Down() exactly.
            // The `configuration` schema itself is deliberately left in
            // place, owned by ihostpro_migrator regardless of this
            // migration's lifecycle. Dropping each table below also drops
            // every index defined on it, including the manually created
            // partial unique index — no separate DROP INDEX needed.
            migrationBuilder.Sql(
                "ALTER DEFAULT PRIVILEGES FOR ROLE ihostpro_migrator IN SCHEMA configuration " +
                "REVOKE SELECT, INSERT, UPDATE, DELETE ON TABLES FROM ihostpro_app;");
            migrationBuilder.Sql("REVOKE ALL ON ALL TABLES IN SCHEMA configuration FROM ihostpro_app;");
            migrationBuilder.Sql("REVOKE USAGE ON SCHEMA configuration FROM ihostpro_app;");

            migrationBuilder.DropTable(
                name: "global_policy_values",
                schema: "configuration");

            migrationBuilder.DropTable(
                name: "policy_audit_log",
                schema: "configuration");

            migrationBuilder.DropTable(
                name: "policy_values",
                schema: "configuration");

            migrationBuilder.DropTable(
                name: "policy_definitions",
                schema: "configuration");
        }
    }
}
