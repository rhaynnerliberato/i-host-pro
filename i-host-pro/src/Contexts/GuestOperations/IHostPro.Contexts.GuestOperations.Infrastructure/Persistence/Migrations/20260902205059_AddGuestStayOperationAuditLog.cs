using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IHostPro.Contexts.GuestOperations.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddGuestStayOperationAuditLog : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "guest_stay_operation_audit_log",
                schema: "guest_operations",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    guest_stay_operation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    action = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    actor_type = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    actor_id = table.Column<Guid>(type: "uuid", nullable: false),
                    occurred_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_guest_stay_operation_audit_log", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_guest_stay_operation_audit_log_tenant_id_guest_stay_operati~",
                schema: "guest_operations",
                table: "guest_stay_operation_audit_log",
                columns: new[] { "tenant_id", "guest_stay_operation_id", "occurred_at_utc" });

            migrationBuilder.CreateIndex(
                name: "IX_guest_stay_operation_audit_log_tenant_id_occurred_at_utc",
                schema: "guest_operations",
                table: "guest_stay_operation_audit_log",
                columns: new[] { "tenant_id", "occurred_at_utc" });

            // --- Row-Level Security (Fase 12, Checkpoint 4 — Guest Access
            // Durable Audit Decision Gate) — byte-for-byte the same
            // fail-closed policy Identity's own AddSecurityAuditLog migration
            // established: current_setting('app.tenant_id', true) returns
            // NULL instead of raising when unset, and `tenant_id = NULL` is
            // never true in SQL, so an unresolved tenant fails closed (zero
            // rows), never with an error. FORCE ROW LEVEL SECURITY applies
            // the policy even to the table owner. WITH CHECK protects
            // INSERT, not only SELECT visibility — an audit row can never be
            // written outside the caller's own resolved tenant.
            migrationBuilder.Sql("ALTER TABLE guest_operations.guest_stay_operation_audit_log ENABLE ROW LEVEL SECURITY;");
            migrationBuilder.Sql("ALTER TABLE guest_operations.guest_stay_operation_audit_log FORCE ROW LEVEL SECURITY;");
            migrationBuilder.Sql("""
                CREATE POLICY tenant_isolation ON guest_operations.guest_stay_operation_audit_log
                    USING (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid)
                    WITH CHECK (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid);
                """);

            // --- Append-only at the database privilege level (mandate §7) ---
            //
            // InitialCreate's own `ALTER DEFAULT PRIVILEGES FOR ROLE
            // ihostpro_migrator IN SCHEMA guest_operations GRANT SELECT,
            // INSERT, UPDATE, DELETE ON TABLES TO ihostpro_app` already
            // applies automatically to this table (Architecture Principles,
            // Section 10) — narrowed here explicitly, since an audit trail
            // that could be mutated after the fact would not be a trail.
            migrationBuilder.Sql("REVOKE UPDATE, DELETE ON guest_operations.guest_stay_operation_audit_log FROM ihostpro_app;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "guest_stay_operation_audit_log",
                schema: "guest_operations");
        }
    }
}
