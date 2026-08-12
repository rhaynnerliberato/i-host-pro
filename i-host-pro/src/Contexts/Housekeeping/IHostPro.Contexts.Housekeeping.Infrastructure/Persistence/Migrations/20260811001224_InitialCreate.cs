using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IHostPro.Contexts.Housekeeping.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "housekeeping");

            migrationBuilder.CreateTable(
                name: "cleaning_audit_log",
                schema: "housekeeping",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    actor_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    entity_type = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    aggregate_id = table.Column<Guid>(type: "uuid", nullable: false),
                    action_code = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    changed_fields = table.Column<string>(type: "jsonb", nullable: false),
                    occurred_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_cleaning_audit_log", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "cleanings",
                schema: "housekeeping",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    property_id = table.Column<Guid>(type: "uuid", nullable: false),
                    reservation_id = table.Column<Guid>(type: "uuid", nullable: true),
                    assigned_housekeeper_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    created_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    started_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    inspection_started_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    completed_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    cancelled_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_cleanings", x => x.id);
                    table.UniqueConstraint("AK_cleanings_tenant_id_id", x => new { x.tenant_id, x.id });
                });

            migrationBuilder.CreateTable(
                name: "property_projection",
                schema: "housekeeping",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    property_id = table.Column<Guid>(type: "uuid", nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_property_projection", x => new { x.tenant_id, x.property_id });
                });

            migrationBuilder.CreateTable(
                name: "reservation_projection",
                schema: "housekeeping",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    reservation_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_reservation_projection", x => new { x.tenant_id, x.reservation_id });
                });

            migrationBuilder.CreateIndex(
                name: "IX_cleaning_audit_log_tenant_id_aggregate_id_occurred_at",
                schema: "housekeeping",
                table: "cleaning_audit_log",
                columns: new[] { "tenant_id", "aggregate_id", "occurred_at" });

            migrationBuilder.CreateIndex(
                name: "IX_cleaning_audit_log_tenant_id_occurred_at",
                schema: "housekeeping",
                table: "cleaning_audit_log",
                columns: new[] { "tenant_id", "occurred_at" });

            migrationBuilder.CreateIndex(
                name: "IX_cleanings_tenant_id_assigned_housekeeper_user_id",
                schema: "housekeeping",
                table: "cleanings",
                columns: new[] { "tenant_id", "assigned_housekeeper_user_id" });

            migrationBuilder.CreateIndex(
                name: "IX_cleanings_tenant_id_property_id_created_at_utc",
                schema: "housekeeping",
                table: "cleanings",
                columns: new[] { "tenant_id", "property_id", "created_at_utc" });

            migrationBuilder.CreateIndex(
                name: "IX_cleanings_tenant_id_reservation_id",
                schema: "housekeeping",
                table: "cleanings",
                columns: new[] { "tenant_id", "reservation_id" });

            migrationBuilder.CreateIndex(
                name: "IX_cleanings_tenant_id_status_created_at_utc",
                schema: "housekeeping",
                table: "cleanings",
                columns: new[] { "tenant_id", "status", "created_at_utc" });

            // --- Least-privilege grants (Fase 6, Incremento 1) ---
            //
            // Mirrors Reservations'/Configuration & Policy's InitialCreate
            // migration exactly: ihostpro_app receives only CONNECT + schema
            // USAGE + the minimum CRUD each table actually needs — never
            // CREATE/ALTER/DROP/TRUNCATE/BYPASSRLS.
            //
            // cleanings: the mutable aggregate — SELECT/INSERT/UPDATE for
            // creation, assignment and every lifecycle transition; never
            // DELETE (no Cleaning is ever hard-deleted).
            //
            // cleaning_audit_log: append-only exactly like
            // reservation_audit_log/policy_audit_log — SELECT/INSERT only,
            // never UPDATE/DELETE.
            //
            // property_projection/reservation_projection: local read models
            // populated exclusively by consuming Property Management's/
            // Reservations' own Integration Events (Checkpoint 0 decision) —
            // SELECT/INSERT/UPDATE for the idempotent upsert both
            // synchronizers perform, never DELETE (a projection entry is
            // never removed, only updated in place).
            migrationBuilder.Sql("REVOKE ALL ON SCHEMA housekeeping FROM PUBLIC;");
            migrationBuilder.Sql("GRANT USAGE ON SCHEMA housekeeping TO ihostpro_app;");

            migrationBuilder.Sql("GRANT SELECT, INSERT, UPDATE ON housekeeping.cleanings TO ihostpro_app;");
            migrationBuilder.Sql("GRANT SELECT, INSERT ON housekeeping.cleaning_audit_log TO ihostpro_app;");
            migrationBuilder.Sql("GRANT SELECT, INSERT, UPDATE ON housekeeping.property_projection TO ihostpro_app;");
            migrationBuilder.Sql("GRANT SELECT, INSERT, UPDATE ON housekeeping.reservation_projection TO ihostpro_app;");

            migrationBuilder.Sql(
                "ALTER DEFAULT PRIVILEGES FOR ROLE ihostpro_migrator IN SCHEMA housekeeping " +
                "GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO ihostpro_app;");

            // --- Row-Level Security (Fase 6, Incremento 1) ---
            //
            // Every table in this schema is tenant-owned. Same
            // current_setting(..., true)/NULLIF fail-closed pattern as every
            // other Bounded Context: absence of a resolved tenant yields zero
            // rows visible/writable, never an error. FORCE applies the policy
            // even to the table owner.
            foreach (var tenantOwnedTable in new[] { "cleanings", "cleaning_audit_log", "property_projection", "reservation_projection" })
            {
                migrationBuilder.Sql($"ALTER TABLE housekeeping.{tenantOwnedTable} ENABLE ROW LEVEL SECURITY;");
                migrationBuilder.Sql($"ALTER TABLE housekeeping.{tenantOwnedTable} FORCE ROW LEVEL SECURITY;");
                migrationBuilder.Sql($"""
                    CREATE POLICY tenant_isolation ON housekeeping.{tenantOwnedTable}
                        USING (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid)
                        WITH CHECK (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid);
                    """);
            }
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Schema-level grant cleanup only — mirrors Reservations'/
            // Configuration & Policy's InitialCreate Down() exactly. The
            // `housekeeping` schema itself is deliberately left in place,
            // owned by ihostpro_migrator regardless of this migration's
            // lifecycle.
            migrationBuilder.Sql(
                "ALTER DEFAULT PRIVILEGES FOR ROLE ihostpro_migrator IN SCHEMA housekeeping " +
                "REVOKE SELECT, INSERT, UPDATE, DELETE ON TABLES FROM ihostpro_app;");
            migrationBuilder.Sql("REVOKE ALL ON ALL TABLES IN SCHEMA housekeeping FROM ihostpro_app;");
            migrationBuilder.Sql("REVOKE USAGE ON SCHEMA housekeeping FROM ihostpro_app;");

            migrationBuilder.DropTable(
                name: "cleaning_audit_log",
                schema: "housekeeping");

            migrationBuilder.DropTable(
                name: "cleanings",
                schema: "housekeeping");

            migrationBuilder.DropTable(
                name: "property_projection",
                schema: "housekeeping");

            migrationBuilder.DropTable(
                name: "reservation_projection",
                schema: "housekeeping");
        }
    }
}
