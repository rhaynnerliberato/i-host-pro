using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IHostPro.Contexts.ExternalIntegrations.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddAirbnbListingTitleMapping : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "airbnb_listing_title_mappings",
                schema: "external_integrations",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    listing_title = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    property_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_airbnb_listing_title_mappings", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_airbnb_listing_title_mappings_tenant_id_listing_title",
                schema: "external_integrations",
                table: "airbnb_listing_title_mappings",
                columns: new[] { "tenant_id", "listing_title" },
                unique: true);

            // --- Row-Level Security ---
            //
            // Tenant-owned table. Same current_setting(..., true)/NULLIF
            // fail-closed pattern, FORCE applied even to the table owner, as
            // every other Bounded Context table — mirrors
            // AddAirbnbIntegrationFoundation exactly. No explicit GRANT
            // needed: InitialCreate's schema-wide ALTER DEFAULT PRIVILEGES
            // already grants ihostpro_app SELECT/INSERT/UPDATE/DELETE on any
            // new table ihostpro_migrator creates in this schema.
            migrationBuilder.Sql("ALTER TABLE external_integrations.airbnb_listing_title_mappings ENABLE ROW LEVEL SECURITY;");
            migrationBuilder.Sql("ALTER TABLE external_integrations.airbnb_listing_title_mappings FORCE ROW LEVEL SECURITY;");
            migrationBuilder.Sql("""
                CREATE POLICY tenant_isolation ON external_integrations.airbnb_listing_title_mappings
                    USING (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid)
                    WITH CHECK (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "airbnb_listing_title_mappings",
                schema: "external_integrations");
        }
    }
}
