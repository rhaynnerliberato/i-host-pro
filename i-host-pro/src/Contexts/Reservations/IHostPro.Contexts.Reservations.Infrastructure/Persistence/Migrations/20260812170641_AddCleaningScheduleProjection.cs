using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IHostPro.Contexts.Reservations.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddCleaningScheduleProjection : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "cleaning_schedule_projection",
                schema: "reservations",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    cleaning_id = table.Column<Guid>(type: "uuid", nullable: false),
                    property_id = table.Column<Guid>(type: "uuid", nullable: false),
                    assigned_housekeeper_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    scheduled_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_cleaning_schedule_projection", x => new { x.tenant_id, x.cleaning_id });
                });

            migrationBuilder.CreateIndex(
                name: "IX_cleaning_schedule_projection_tenant_id_property_id_schedule~",
                schema: "reservations",
                table: "cleaning_schedule_projection",
                columns: new[] { "tenant_id", "property_id", "scheduled_at_utc" });

            migrationBuilder.CreateIndex(
                name: "IX_cleaning_schedule_projection_tenant_id_scheduled_at_utc_cle~",
                schema: "reservations",
                table: "cleaning_schedule_projection",
                columns: new[] { "tenant_id", "scheduled_at_utc", "cleaning_id" });

            // The schema-wide ALTER DEFAULT PRIVILEGES from InitialCreate
            // already granted ihostpro_app SELECT/INSERT/UPDATE/DELETE on
            // this new table automatically — revoke DELETE here: same
            // update-in-place-never-removed convention as Housekeeping's own
            // property_projection (a projection row is created once by
            // CleaningCreated and only ever updated by later Cleaning
            // events, never deleted).
            migrationBuilder.Sql("REVOKE DELETE ON reservations.cleaning_schedule_projection FROM ihostpro_app;");

            // --- Row-Level Security (Fase 7, Incremento 1 — Agenda
            // Foundation) --- same pattern as every other tenant-owned table
            // in this schema (InitialCreate).
            migrationBuilder.Sql("ALTER TABLE reservations.cleaning_schedule_projection ENABLE ROW LEVEL SECURITY;");
            migrationBuilder.Sql("ALTER TABLE reservations.cleaning_schedule_projection FORCE ROW LEVEL SECURITY;");
            migrationBuilder.Sql("""
                CREATE POLICY tenant_isolation ON reservations.cleaning_schedule_projection
                    USING (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid)
                    WITH CHECK (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "cleaning_schedule_projection",
                schema: "reservations");
        }
    }
}
