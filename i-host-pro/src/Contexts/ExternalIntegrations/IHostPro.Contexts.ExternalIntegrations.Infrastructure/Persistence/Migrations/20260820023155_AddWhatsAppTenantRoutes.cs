using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IHostPro.Contexts.ExternalIntegrations.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddWhatsAppTenantRoutes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "whatsapp_tenant_routes",
                schema: "external_integrations",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    phone_number_id = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_whatsapp_tenant_routes", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_whatsapp_tenant_routes_phone_number_id",
                schema: "external_integrations",
                table: "whatsapp_tenant_routes",
                column: "phone_number_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_whatsapp_tenant_routes_tenant_id",
                schema: "external_integrations",
                table: "whatsapp_tenant_routes",
                column: "tenant_id",
                unique: true);

            // --- Least-privilege grant (Fase 9, Checkpoint 2.3.2) ---
            //
            // Explicit, matching whatsapp_integrations' own InitialCreate
            // grant exactly (technically redundant with InitialCreate's
            // ALTER DEFAULT PRIVILEGES for ihostpro_migrator-created tables,
            // kept for the same clarity/consistency reason that grant
            // already exists per-table elsewhere in this schema). Never
            // CREATE/ALTER/DROP/TRUNCATE/BYPASSRLS.
            migrationBuilder.Sql("GRANT SELECT, INSERT, UPDATE, DELETE ON external_integrations.whatsapp_tenant_routes TO ihostpro_app;");

            // --- Deliberately NO Row-Level Security here (ADR-022, items 10-12) ---
            //
            // whatsapp_tenant_routes is the global routing directory that
            // exists specifically to answer "which tenant" BEFORE any
            // TenantId is known — an RLS-protected table cannot do that by
            // definition (a session with no app.tenant_id set would see
            // zero rows, defeating the table's entire purpose). No
            // ENABLE/FORCE ROW LEVEL SECURITY, no CREATE POLICY, no
            // BYPASSRLS anywhere. Unlike whatsapp_integrations
            // (tenant-owned, RLS-protected), this table holds only
            // identifiers — no secret, no raw phone number, no webhook
            // payload — so global readability by the app role carries no
            // secret/PII exposure risk.
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("REVOKE ALL ON external_integrations.whatsapp_tenant_routes FROM ihostpro_app;");

            migrationBuilder.DropTable(
                name: "whatsapp_tenant_routes",
                schema: "external_integrations");
        }
    }
}
