using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IHostPro.Contexts.GuestOperations.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "guest_operations");

            migrationBuilder.CreateTable(
                name: "guest_stay_operations",
                schema: "guest_operations",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    reservation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    property_id = table.Column<Guid>(type: "uuid", nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    checked_in_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    checked_out_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_guest_stay_operations", x => x.id);
                    table.UniqueConstraint("AK_guest_stay_operations_tenant_id_id", x => new { x.tenant_id, x.id });
                });

            migrationBuilder.CreateIndex(
                name: "IX_guest_stay_operations_tenant_id_reservation_id",
                schema: "guest_operations",
                table: "guest_stay_operations",
                columns: new[] { "tenant_id", "reservation_id" },
                unique: true);

            // --- Least-privilege grants (Fase 10, Checkpoint 1 — Guest
            // Operations Foundation) ---
            //
            // Mirrors every other Bounded Context's own InitialCreate
            // migration exactly: ihostpro_app receives only CONNECT + schema
            // USAGE + the minimum CRUD guest_stay_operations actually needs
            // — never CREATE/ALTER/DROP/TRUNCATE/BYPASSRLS.
            migrationBuilder.Sql("REVOKE ALL ON SCHEMA guest_operations FROM PUBLIC;");
            migrationBuilder.Sql("GRANT USAGE ON SCHEMA guest_operations TO ihostpro_app;");

            migrationBuilder.Sql("GRANT SELECT, INSERT, UPDATE ON guest_operations.guest_stay_operations TO ihostpro_app;");

            migrationBuilder.Sql(
                "ALTER DEFAULT PRIVILEGES FOR ROLE ihostpro_migrator IN SCHEMA guest_operations " +
                "GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO ihostpro_app;");

            // --- Row-Level Security (Fase 10, Checkpoint 1) ---
            //
            // guest_stay_operations is tenant-owned. Same current_setting(...,
            // true)/NULLIF fail-closed pattern as every other Bounded
            // Context: absence of a resolved tenant yields zero rows
            // visible/writable, never an error. FORCE applies the policy even
            // to the table owner (ihostpro_migrator has no BYPASSRLS —
            // Architecture Principles, Section 10).
            migrationBuilder.Sql("ALTER TABLE guest_operations.guest_stay_operations ENABLE ROW LEVEL SECURITY;");
            migrationBuilder.Sql("ALTER TABLE guest_operations.guest_stay_operations FORCE ROW LEVEL SECURITY;");
            migrationBuilder.Sql("""
                CREATE POLICY tenant_isolation ON guest_operations.guest_stay_operations
                    USING (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid)
                    WITH CHECK (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Schema-level grant cleanup only — mirrors every other Bounded
            // Context's InitialCreate Down() exactly. The `guest_operations`
            // schema itself is deliberately left in place, owned by
            // ihostpro_migrator regardless of this migration's lifecycle.
            migrationBuilder.Sql(
                "ALTER DEFAULT PRIVILEGES FOR ROLE ihostpro_migrator IN SCHEMA guest_operations " +
                "REVOKE SELECT, INSERT, UPDATE, DELETE ON TABLES FROM ihostpro_app;");
            migrationBuilder.Sql("REVOKE ALL ON ALL TABLES IN SCHEMA guest_operations FROM ihostpro_app;");
            migrationBuilder.Sql("REVOKE USAGE ON SCHEMA guest_operations FROM ihostpro_app;");

            migrationBuilder.DropTable(
                name: "guest_stay_operations",
                schema: "guest_operations");
        }
    }
}
