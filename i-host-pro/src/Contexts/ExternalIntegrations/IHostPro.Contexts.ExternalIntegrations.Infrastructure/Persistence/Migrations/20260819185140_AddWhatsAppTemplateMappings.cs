using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IHostPro.Contexts.ExternalIntegrations.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddWhatsAppTemplateMappings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "whatsapp_template_mappings",
                schema: "external_integrations",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    template_key = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    provider_template_name = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    language_code = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    parameter_order = table.Column<string>(type: "jsonb", nullable: false),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_whatsapp_template_mappings", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_whatsapp_template_mappings_tenant_id_template_key",
                schema: "external_integrations",
                table: "whatsapp_template_mappings",
                columns: new[] { "tenant_id", "template_key" },
                unique: true);

            // --- Row-Level Security (Fase 9, Checkpoint 2.2) ---
            //
            // whatsapp_template_mappings is tenant-owned — same
            // current_setting(..., true)/NULLIF fail-closed pattern, FORCE
            // applied even to the table owner, mirrors InitialCreate exactly.
            // No explicit GRANT needed: InitialCreate's schema-wide ALTER
            // DEFAULT PRIVILEGES already grants ihostpro_app SELECT/INSERT/
            // UPDATE/DELETE on any new table ihostpro_migrator creates in
            // this schema (mirrors Housekeeping's AddCleaningChecklistItems
            // precedent exactly).
            migrationBuilder.Sql("ALTER TABLE external_integrations.whatsapp_template_mappings ENABLE ROW LEVEL SECURITY;");
            migrationBuilder.Sql("ALTER TABLE external_integrations.whatsapp_template_mappings FORCE ROW LEVEL SECURITY;");
            migrationBuilder.Sql("""
                CREATE POLICY tenant_isolation ON external_integrations.whatsapp_template_mappings
                    USING (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid)
                    WITH CHECK (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "whatsapp_template_mappings",
                schema: "external_integrations");
        }
    }
}
