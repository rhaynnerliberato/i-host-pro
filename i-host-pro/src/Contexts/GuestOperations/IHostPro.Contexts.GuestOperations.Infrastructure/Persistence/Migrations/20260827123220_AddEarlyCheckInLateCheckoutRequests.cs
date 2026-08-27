using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IHostPro.Contexts.GuestOperations.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddEarlyCheckInLateCheckoutRequests : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "early_check_in_requests",
                schema: "guest_operations",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    reservation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    property_id = table.Column<Guid>(type: "uuid", nullable: false),
                    requested_check_in_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    denial_reason = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: true),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    decided_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    updated_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_early_check_in_requests", x => x.id);
                    table.UniqueConstraint("AK_early_check_in_requests_tenant_id_id", x => new { x.tenant_id, x.id });
                });

            migrationBuilder.CreateTable(
                name: "late_checkout_requests",
                schema: "guest_operations",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    reservation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    property_id = table.Column<Guid>(type: "uuid", nullable: false),
                    requested_check_out_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    charge_type = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    charge_value = table.Column<decimal>(type: "numeric(12,2)", nullable: true),
                    requires_pix = table.Column<bool>(type: "boolean", nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    denial_reason = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: true),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    decided_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    updated_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_late_checkout_requests", x => x.id);
                    table.UniqueConstraint("AK_late_checkout_requests_tenant_id_id", x => new { x.tenant_id, x.id });
                });

            migrationBuilder.CreateIndex(
                name: "ix_early_check_in_requests_tenant_id_reservation_id_pending_unique",
                schema: "guest_operations",
                table: "early_check_in_requests",
                columns: new[] { "tenant_id", "reservation_id" },
                unique: true,
                filter: "status = 'Pending'");

            migrationBuilder.CreateIndex(
                name: "ix_late_checkout_requests_tenant_id_reservation_id_active_unique",
                schema: "guest_operations",
                table: "late_checkout_requests",
                columns: new[] { "tenant_id", "reservation_id" },
                unique: true,
                filter: "status IN ('Pending', 'PendingPayment')");

            // --- Least-privilege grants (Fase 10, Checkpoint 3 — Early
            // Check-in / Late Checkout) ---
            //
            // InitialCreate's own ALTER DEFAULT PRIVILEGES FOR ROLE
            // ihostpro_migrator IN SCHEMA guest_operations already grants
            // ihostpro_app SELECT/INSERT/UPDATE/DELETE on these two brand-new
            // tables automatically (mirrors Housekeeping's own
            // AddCleaningOccurrences precedent) — DELETE is revoked here to
            // match guest_stay_operations' own least-privilege convention
            // (SELECT/INSERT/UPDATE only; neither request type is ever
            // deleted, only created and updated in place).
            migrationBuilder.Sql("REVOKE DELETE ON guest_operations.early_check_in_requests FROM ihostpro_app;");
            migrationBuilder.Sql("REVOKE DELETE ON guest_operations.late_checkout_requests FROM ihostpro_app;");

            // --- Row-Level Security (Fase 10, Checkpoint 3) ---
            //
            // Both tables are tenant-owned. Same current_setting(...,
            // true)/NULLIF fail-closed pattern, and FORCE applies the policy
            // even to the table owner — mirrors InitialCreate exactly.
            migrationBuilder.Sql("ALTER TABLE guest_operations.early_check_in_requests ENABLE ROW LEVEL SECURITY;");
            migrationBuilder.Sql("ALTER TABLE guest_operations.early_check_in_requests FORCE ROW LEVEL SECURITY;");
            migrationBuilder.Sql("""
                CREATE POLICY tenant_isolation ON guest_operations.early_check_in_requests
                    USING (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid)
                    WITH CHECK (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid);
                """);

            migrationBuilder.Sql("ALTER TABLE guest_operations.late_checkout_requests ENABLE ROW LEVEL SECURITY;");
            migrationBuilder.Sql("ALTER TABLE guest_operations.late_checkout_requests FORCE ROW LEVEL SECURITY;");
            migrationBuilder.Sql("""
                CREATE POLICY tenant_isolation ON guest_operations.late_checkout_requests
                    USING (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid)
                    WITH CHECK (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "early_check_in_requests",
                schema: "guest_operations");

            migrationBuilder.DropTable(
                name: "late_checkout_requests",
                schema: "guest_operations");
        }
    }
}
