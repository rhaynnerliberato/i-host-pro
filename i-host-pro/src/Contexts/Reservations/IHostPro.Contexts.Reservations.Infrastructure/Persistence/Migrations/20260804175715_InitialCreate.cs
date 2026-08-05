using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IHostPro.Contexts.Reservations.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "reservations");

            migrationBuilder.CreateTable(
                name: "reservation_audit_log",
                schema: "reservations",
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
                    table.PrimaryKey("PK_reservation_audit_log", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "reservations",
                schema: "reservations",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    property_id = table.Column<Guid>(type: "uuid", nullable: false),
                    guest_name = table.Column<string>(type: "character varying(150)", maxLength: 150, nullable: false),
                    guest_phone = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: true),
                    check_in_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    check_out_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    guest_count = table.Column<int>(type: "integer", nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_reservations", x => x.id);
                    table.UniqueConstraint("AK_reservations_tenant_id_id", x => new { x.tenant_id, x.id });
                });

            migrationBuilder.CreateIndex(
                name: "IX_reservation_audit_log_tenant_id_aggregate_id_occurred_at",
                schema: "reservations",
                table: "reservation_audit_log",
                columns: new[] { "tenant_id", "aggregate_id", "occurred_at" });

            migrationBuilder.CreateIndex(
                name: "IX_reservation_audit_log_tenant_id_occurred_at",
                schema: "reservations",
                table: "reservation_audit_log",
                columns: new[] { "tenant_id", "occurred_at" });

            migrationBuilder.CreateIndex(
                name: "IX_reservations_tenant_id_check_in_at_id",
                schema: "reservations",
                table: "reservations",
                columns: new[] { "tenant_id", "check_in_at", "id" });

            migrationBuilder.CreateIndex(
                name: "IX_reservations_tenant_id_property_id_check_in_at",
                schema: "reservations",
                table: "reservations",
                columns: new[] { "tenant_id", "property_id", "check_in_at" });

            migrationBuilder.CreateIndex(
                name: "IX_reservations_tenant_id_status_check_in_at",
                schema: "reservations",
                table: "reservations",
                columns: new[] { "tenant_id", "status", "check_in_at" });

            // --- Least-privilege grants (Fase 3, Incremento 1 plan) ---
            //
            // Mirrors Property Management's/Identity's InitialCreate migration
            // exactly: ihostpro_app (IHostPro.Api/IHostPro.Worker) receives
            // only CONNECT + schema USAGE + CRUD on tables — never
            // CREATE/ALTER/DROP/TRUNCATE/BYPASSRLS. ihostpro_migrator
            // (IHostPro.MigrationRunner) owns the schema/tables by having
            // executed this migration. Neither role is created/altered/dropped
            // here — role lifecycle stays exclusively
            // docker/postgres/init/01-create-roles.sh's responsibility.
            //
            // reservation_audit_log is the one exception: append-only per
            // item 11 — ihostpro_app gets SELECT/INSERT only, never
            // UPDATE/DELETE, so no application code path can alter or erase
            // an audit row even by accident.
            migrationBuilder.Sql("REVOKE ALL ON SCHEMA reservations FROM PUBLIC;");
            migrationBuilder.Sql("GRANT USAGE ON SCHEMA reservations TO ihostpro_app;");

            migrationBuilder.Sql("GRANT SELECT, INSERT, UPDATE, DELETE ON reservations.reservations TO ihostpro_app;");
            migrationBuilder.Sql(
                "GRANT SELECT, INSERT ON reservations.reservation_audit_log TO ihostpro_app;");

            migrationBuilder.Sql(
                "ALTER DEFAULT PRIVILEGES FOR ROLE ihostpro_migrator IN SCHEMA reservations " +
                "GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO ihostpro_app;");

            // --- Row-Level Security (Fase 3, Incremento 1 plan) ---
            //
            // Both tables in this schema are tenant-owned. Same
            // current_setting(..., true)/NULLIF fail-closed pattern as
            // Identity/Property Management: absence of a resolved tenant
            // yields zero rows visible/writable, never an error. FORCE
            // applies the policy even to the table owner.
            foreach (var tenantOwnedTable in new[] { "reservations", "reservation_audit_log" })
            {
                migrationBuilder.Sql($"ALTER TABLE reservations.{tenantOwnedTable} ENABLE ROW LEVEL SECURITY;");
                migrationBuilder.Sql($"ALTER TABLE reservations.{tenantOwnedTable} FORCE ROW LEVEL SECURITY;");
                migrationBuilder.Sql($"""
                    CREATE POLICY tenant_isolation ON reservations.{tenantOwnedTable}
                        USING (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid)
                        WITH CHECK (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid);
                    """);
            }
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Schema-level grant cleanup only — mirrors Property Management's/
            // Identity's InitialCreate Down() exactly. The `reservations`
            // schema itself is deliberately left in place, owned by
            // ihostpro_migrator regardless of this migration's lifecycle.
            migrationBuilder.Sql(
                "ALTER DEFAULT PRIVILEGES FOR ROLE ihostpro_migrator IN SCHEMA reservations " +
                "REVOKE SELECT, INSERT, UPDATE, DELETE ON TABLES FROM ihostpro_app;");
            migrationBuilder.Sql("REVOKE ALL ON ALL TABLES IN SCHEMA reservations FROM ihostpro_app;");
            migrationBuilder.Sql("REVOKE USAGE ON SCHEMA reservations FROM ihostpro_app;");

            migrationBuilder.DropTable(
                name: "reservation_audit_log",
                schema: "reservations");

            migrationBuilder.DropTable(
                name: "reservations",
                schema: "reservations");
        }
    }
}
