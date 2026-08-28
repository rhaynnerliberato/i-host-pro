using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IHostPro.Contexts.PropertyManagement.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddPropertyAccessConfiguration : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "property_access_configurations",
                schema: "property_management",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    property_id = table.Column<Guid>(type: "uuid", nullable: false),
                    access_credential_secret_reference = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    access_instructions = table.Column<string>(type: "text", nullable: true),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_property_access_configurations", x => x.id);
                    table.UniqueConstraint("AK_property_access_configurations_tenant_id_id", x => new { x.tenant_id, x.id });
                    table.ForeignKey(
                        name: "FK_property_access_configurations_properties_tenant_id_propert~",
                        columns: x => new { x.tenant_id, x.property_id },
                        principalSchema: "property_management",
                        principalTable: "properties",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_property_access_configurations_tenant_id_property_id_unique",
                schema: "property_management",
                table: "property_access_configurations",
                columns: new[] { "tenant_id", "property_id" },
                unique: true);

            // --- Least-privilege grant (Fase 10, Checkpoint 6.2) ---
            //
            // InitialCreate's own "ALTER DEFAULT PRIVILEGES FOR ROLE
            // ihostpro_migrator IN SCHEMA property_management ... TO
            // ihostpro_app" already auto-grants SELECT/INSERT/UPDATE/DELETE
            // to ihostpro_app on this brand-new table — mirrors
            // AddFrontDeskContact's own migration precedent exactly.
            // property_access_configurations is updated in place
            // (PropertyAccessConfiguration.UpdateConfiguration), never
            // soft-deleted and re-created — REVOKE DELETE reaches the
            // intended least-privilege end state without a redundant
            // explicit GRANT.
            migrationBuilder.Sql("REVOKE DELETE ON property_management.property_access_configurations FROM ihostpro_app;");

            // --- Row-Level Security (Fase 10, Checkpoint 6.2) ---
            //
            // Same current_setting(..., true)/NULLIF fail-closed pattern as
            // every other tenant-owned table in this schema.
            migrationBuilder.Sql("ALTER TABLE property_management.property_access_configurations ENABLE ROW LEVEL SECURITY;");
            migrationBuilder.Sql("ALTER TABLE property_management.property_access_configurations FORCE ROW LEVEL SECURITY;");
            migrationBuilder.Sql("""
                CREATE POLICY tenant_isolation ON property_management.property_access_configurations
                    USING (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid)
                    WITH CHECK (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("GRANT DELETE ON property_management.property_access_configurations TO ihostpro_app;");

            migrationBuilder.DropTable(
                name: "property_access_configurations",
                schema: "property_management");
        }
    }
}
