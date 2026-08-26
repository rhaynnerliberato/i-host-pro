using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IHostPro.Contexts.ExternalIntegrations.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddAirbnbIntegrationFoundation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "airbnb_integrations",
                schema: "external_integrations",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    external_account_id = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    is_enabled = table.Column<bool>(type: "boolean", nullable: false),
                    credential_secret_reference = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_airbnb_integrations", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "airbnb_listing_mappings",
                schema: "external_integrations",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    airbnb_integration_id = table.Column<Guid>(type: "uuid", nullable: false),
                    external_listing_id = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    property_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_airbnb_listing_mappings", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_airbnb_integrations_tenant_id",
                schema: "external_integrations",
                table: "airbnb_integrations",
                column: "tenant_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_airbnb_listing_mappings_tenant_id_external_listing_id",
                schema: "external_integrations",
                table: "airbnb_listing_mappings",
                columns: new[] { "tenant_id", "external_listing_id" },
                unique: true);

            // --- Row-Level Security (Fase 9, Checkpoint 3.2) ---
            //
            // Both tables are tenant-owned. Same current_setting(...,
            // true)/NULLIF fail-closed pattern, FORCE applied even to the
            // table owner, as every other Bounded Context — mirrors
            // AddWhatsAppTemplateMappings exactly. No explicit GRANT needed:
            // InitialCreate's schema-wide ALTER DEFAULT PRIVILEGES already
            // grants ihostpro_app SELECT/INSERT/UPDATE/DELETE on any new
            // table ihostpro_migrator creates in this schema.
            migrationBuilder.Sql("ALTER TABLE external_integrations.airbnb_integrations ENABLE ROW LEVEL SECURITY;");
            migrationBuilder.Sql("ALTER TABLE external_integrations.airbnb_integrations FORCE ROW LEVEL SECURITY;");
            migrationBuilder.Sql("""
                CREATE POLICY tenant_isolation ON external_integrations.airbnb_integrations
                    USING (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid)
                    WITH CHECK (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid);
                """);

            migrationBuilder.Sql("ALTER TABLE external_integrations.airbnb_listing_mappings ENABLE ROW LEVEL SECURITY;");
            migrationBuilder.Sql("ALTER TABLE external_integrations.airbnb_listing_mappings FORCE ROW LEVEL SECURITY;");
            migrationBuilder.Sql("""
                CREATE POLICY tenant_isolation ON external_integrations.airbnb_listing_mappings
                    USING (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid)
                    WITH CHECK (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "airbnb_integrations",
                schema: "external_integrations");

            migrationBuilder.DropTable(
                name: "airbnb_listing_mappings",
                schema: "external_integrations");
        }
    }
}
