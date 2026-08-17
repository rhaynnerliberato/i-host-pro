using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IHostPro.Contexts.Dashboard.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "dashboard");

            migrationBuilder.CreateTable(
                name: "cleaning_projection",
                schema: "dashboard",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    cleaning_id = table.Column<Guid>(type: "uuid", nullable: false),
                    property_id = table.Column<Guid>(type: "uuid", nullable: false),
                    housekeeper_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    scheduled_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    started_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    completed_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_event_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_cleaning_projection", x => new { x.tenant_id, x.cleaning_id });
                });

            migrationBuilder.CreateTable(
                name: "occurrence_projection",
                schema: "dashboard",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    occurrence_id = table.Column<Guid>(type: "uuid", nullable: false),
                    cleaning_id = table.Column<Guid>(type: "uuid", nullable: false),
                    type = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    registered_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_occurrence_projection", x => new { x.tenant_id, x.occurrence_id });
                });

            migrationBuilder.CreateTable(
                name: "property_projection",
                schema: "dashboard",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    property_id = table.Column<Guid>(type: "uuid", nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    last_event_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_property_projection", x => new { x.tenant_id, x.property_id });
                });

            migrationBuilder.CreateTable(
                name: "reservation_projection",
                schema: "dashboard",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    reservation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    property_id = table.Column<Guid>(type: "uuid", nullable: false),
                    check_in_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    check_out_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    last_event_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_reservation_projection", x => new { x.tenant_id, x.reservation_id });
                });

            migrationBuilder.CreateIndex(
                name: "IX_cleaning_projection_tenant_id_property_id",
                schema: "dashboard",
                table: "cleaning_projection",
                columns: new[] { "tenant_id", "property_id" });

            migrationBuilder.CreateIndex(
                name: "IX_cleaning_projection_tenant_id_scheduled_at_utc",
                schema: "dashboard",
                table: "cleaning_projection",
                columns: new[] { "tenant_id", "scheduled_at_utc" });

            migrationBuilder.CreateIndex(
                name: "IX_cleaning_projection_tenant_id_status",
                schema: "dashboard",
                table: "cleaning_projection",
                columns: new[] { "tenant_id", "status" });

            migrationBuilder.CreateIndex(
                name: "IX_occurrence_projection_tenant_id_cleaning_id",
                schema: "dashboard",
                table: "occurrence_projection",
                columns: new[] { "tenant_id", "cleaning_id" });

            migrationBuilder.CreateIndex(
                name: "IX_occurrence_projection_tenant_id_registered_at_utc",
                schema: "dashboard",
                table: "occurrence_projection",
                columns: new[] { "tenant_id", "registered_at_utc" });

            migrationBuilder.CreateIndex(
                name: "IX_occurrence_projection_tenant_id_type",
                schema: "dashboard",
                table: "occurrence_projection",
                columns: new[] { "tenant_id", "type" });

            migrationBuilder.CreateIndex(
                name: "IX_property_projection_tenant_id_status",
                schema: "dashboard",
                table: "property_projection",
                columns: new[] { "tenant_id", "status" });

            migrationBuilder.CreateIndex(
                name: "IX_reservation_projection_tenant_id_check_in_at",
                schema: "dashboard",
                table: "reservation_projection",
                columns: new[] { "tenant_id", "check_in_at" });

            migrationBuilder.CreateIndex(
                name: "IX_reservation_projection_tenant_id_check_out_at",
                schema: "dashboard",
                table: "reservation_projection",
                columns: new[] { "tenant_id", "check_out_at" });

            migrationBuilder.CreateIndex(
                name: "IX_reservation_projection_tenant_id_status",
                schema: "dashboard",
                table: "reservation_projection",
                columns: new[] { "tenant_id", "status" });

            // --- Least-privilege grants (Fase 7, Incremento 2 — Dashboard &
            // Reporting Foundation, Checkpoint 1) ---
            //
            // Mirrors every other Bounded Context's InitialCreate migration
            // exactly: ihostpro_app receives only CONNECT + schema USAGE +
            // the minimum CRUD each table actually needs — never
            // CREATE/ALTER/DROP/TRUNCATE/BYPASSRLS. All four projection
            // tables are synchronizer-managed only (created once, updated
            // in place by later events, never deleted — mirrors
            // Housekeeping's own property_projection/Reservations' own
            // cleaning_schedule_projection convention) — no DELETE granted.
            migrationBuilder.Sql("REVOKE ALL ON SCHEMA dashboard FROM PUBLIC;");
            migrationBuilder.Sql("GRANT USAGE ON SCHEMA dashboard TO ihostpro_app;");

            migrationBuilder.Sql("GRANT SELECT, INSERT, UPDATE ON dashboard.reservation_projection TO ihostpro_app;");
            migrationBuilder.Sql("GRANT SELECT, INSERT, UPDATE ON dashboard.cleaning_projection TO ihostpro_app;");
            migrationBuilder.Sql("GRANT SELECT, INSERT, UPDATE ON dashboard.property_projection TO ihostpro_app;");
            migrationBuilder.Sql("GRANT SELECT, INSERT ON dashboard.occurrence_projection TO ihostpro_app;");

            migrationBuilder.Sql(
                "ALTER DEFAULT PRIVILEGES FOR ROLE ihostpro_migrator IN SCHEMA dashboard " +
                "GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO ihostpro_app;");

            // --- Row-Level Security (Fase 7, Incremento 2, Checkpoint 1) ---
            //
            // Every table here is tenant-owned. Same current_setting(...,
            // true)/NULLIF fail-closed pattern as every other Bounded
            // Context: absence of a resolved tenant yields zero rows
            // visible/writable, never an error. FORCE applies the policy
            // even to the table owner (ihostpro_migrator has no BYPASSRLS —
            // Architecture Principles, Section 10).
            foreach (var tenantOwnedTable in new[]
                { "reservation_projection", "cleaning_projection", "property_projection", "occurrence_projection" })
            {
                migrationBuilder.Sql($"ALTER TABLE dashboard.{tenantOwnedTable} ENABLE ROW LEVEL SECURITY;");
                migrationBuilder.Sql($"ALTER TABLE dashboard.{tenantOwnedTable} FORCE ROW LEVEL SECURITY;");
                migrationBuilder.Sql($"""
                    CREATE POLICY tenant_isolation ON dashboard.{tenantOwnedTable}
                        USING (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid)
                        WITH CHECK (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid);
                    """);
            }
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Schema-level grant cleanup only — mirrors every other Bounded
            // Context's InitialCreate Down() exactly. The `dashboard` schema
            // itself is deliberately left in place, owned by
            // ihostpro_migrator regardless of this migration's lifecycle.
            // Dropping each table below also drops every RLS policy and
            // index defined on it — no separate DROP POLICY/DROP INDEX
            // needed.
            migrationBuilder.Sql(
                "ALTER DEFAULT PRIVILEGES FOR ROLE ihostpro_migrator IN SCHEMA dashboard " +
                "REVOKE SELECT, INSERT, UPDATE, DELETE ON TABLES FROM ihostpro_app;");
            migrationBuilder.Sql("REVOKE ALL ON ALL TABLES IN SCHEMA dashboard FROM ihostpro_app;");
            migrationBuilder.Sql("REVOKE USAGE ON SCHEMA dashboard FROM ihostpro_app;");

            migrationBuilder.DropTable(
                name: "cleaning_projection",
                schema: "dashboard");

            migrationBuilder.DropTable(
                name: "occurrence_projection",
                schema: "dashboard");

            migrationBuilder.DropTable(
                name: "property_projection",
                schema: "dashboard");

            migrationBuilder.DropTable(
                name: "reservation_projection",
                schema: "dashboard");
        }
    }
}
