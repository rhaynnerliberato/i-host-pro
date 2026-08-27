using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IHostPro.Contexts.PropertyManagement.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddFrontDeskContact : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "front_desk_contacts",
                schema: "property_management",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    condominium_id = table.Column<Guid>(type: "uuid", nullable: false),
                    display_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    phone_number = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_front_desk_contacts", x => x.id);
                    table.UniqueConstraint("AK_front_desk_contacts_tenant_id_id", x => new { x.tenant_id, x.id });
                    table.ForeignKey(
                        name: "FK_front_desk_contacts_condominiums_tenant_id_condominium_id",
                        columns: x => new { x.tenant_id, x.condominium_id },
                        principalSchema: "property_management",
                        principalTable: "condominiums",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_front_desk_contacts_tenant_id_condominium_id_unique",
                schema: "property_management",
                table: "front_desk_contacts",
                columns: new[] { "tenant_id", "condominium_id" },
                unique: true);

            // --- Least-privilege grant (Fase 10, Checkpoint 4) ---
            //
            // InitialCreate's own "ALTER DEFAULT PRIVILEGES FOR ROLE
            // ihostpro_migrator IN SCHEMA property_management ... TO
            // ihostpro_app" already auto-grants SELECT/INSERT/UPDATE/DELETE
            // to ihostpro_app on this brand-new table — mirrors
            // GuestOperations' own AddEarlyCheckInLateCheckoutRequests
            // migration precedent exactly. front_desk_contacts is updated in
            // place (FrontDeskContact.UpdateContact), never soft-deleted and
            // re-created — REVOKE DELETE reaches the intended least-privilege
            // end state without a redundant explicit GRANT.
            migrationBuilder.Sql("REVOKE DELETE ON property_management.front_desk_contacts FROM ihostpro_app;");

            // --- Row-Level Security (Fase 10, Checkpoint 4) ---
            //
            // Same current_setting(..., true)/NULLIF fail-closed pattern as
            // every other tenant-owned table in this schema.
            migrationBuilder.Sql("ALTER TABLE property_management.front_desk_contacts ENABLE ROW LEVEL SECURITY;");
            migrationBuilder.Sql("ALTER TABLE property_management.front_desk_contacts FORCE ROW LEVEL SECURITY;");
            migrationBuilder.Sql("""
                CREATE POLICY tenant_isolation ON property_management.front_desk_contacts
                    USING (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid)
                    WITH CHECK (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("GRANT DELETE ON property_management.front_desk_contacts TO ihostpro_app;");

            migrationBuilder.DropTable(
                name: "front_desk_contacts",
                schema: "property_management");
        }
    }
}
