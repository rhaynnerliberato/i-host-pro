using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IHostPro.Contexts.Identity.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddSecurityAuditLog : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "security_audit_log",
                schema: "identity",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    event_type = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    reason_code = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    occurred_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    session_id = table.Column<Guid>(type: "uuid", nullable: true),
                    refresh_token_id = table.Column<Guid>(type: "uuid", nullable: true),
                    ip_address = table.Column<string>(type: "character varying(45)", maxLength: 45, nullable: true),
                    correlation_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_security_audit_log", x => x.id);
                    table.ForeignKey(
                        name: "FK_security_audit_log_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalSchema: "identity",
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_security_audit_log_tenant_id_occurred_at",
                schema: "identity",
                table: "security_audit_log",
                columns: new[] { "tenant_id", "occurred_at" });

            migrationBuilder.CreateIndex(
                name: "IX_security_audit_log_tenant_id_session_id_occurred_at",
                schema: "identity",
                table: "security_audit_log",
                columns: new[] { "tenant_id", "session_id", "occurred_at" });

            migrationBuilder.CreateIndex(
                name: "IX_security_audit_log_tenant_id_user_id_occurred_at",
                schema: "identity",
                table: "security_audit_log",
                columns: new[] { "tenant_id", "user_id", "occurred_at" });

            // --- Row-Level Security (Incremento 2 plan, ajuste 1) ---
            //
            // security_audit_log is tenant-owned like users/user_roles/
            // sessions/refresh_tokens (InitialCreate) — same fail-closed
            // policy, byte-for-byte: current_setting('app.tenant_id', true)
            // returns NULL instead of raising when unset, and
            // `tenant_id = NULL` is never true in SQL, so the absence of a
            // resolved tenant fails closed (zero rows), never with an error.
            // FORCE ROW LEVEL SECURITY applies the policy even to the table
            // owner. WITH CHECK protects INSERT, not only SELECT/DELETE
            // visibility — an audit row can never be written outside the
            // caller's own resolved tenant.
            //
            // No GRANT/ALTER DEFAULT PRIVILEGES statement is needed here:
            // InitialCreate already configured
            // `ALTER DEFAULT PRIVILEGES FOR ROLE ihostpro_migrator IN SCHEMA
            // identity GRANT ... TO ihostpro_app`, which applies automatically
            // to every table this role creates from then on, including this
            // one (Architecture Principles, Section 10) — verified empirically
            // during homologation of this migration, not merely assumed.
            migrationBuilder.Sql("ALTER TABLE identity.security_audit_log ENABLE ROW LEVEL SECURITY;");
            migrationBuilder.Sql("ALTER TABLE identity.security_audit_log FORCE ROW LEVEL SECURITY;");
            migrationBuilder.Sql("""
                CREATE POLICY tenant_isolation ON identity.security_audit_log
                    USING (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid)
                    WITH CHECK (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "security_audit_log",
                schema: "identity");
        }
    }
}
