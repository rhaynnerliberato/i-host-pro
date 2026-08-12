using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IHostPro.Contexts.Housekeeping.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddCleaningChecklistItems : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "cleaning_checklist_items",
                schema: "housekeeping",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    cleaning_id = table.Column<Guid>(type: "uuid", nullable: false),
                    item_type = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    is_checked = table.Column<bool>(type: "boolean", nullable: false),
                    updated_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    updated_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_cleaning_checklist_items", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_cleaning_checklist_items_tenant_id_cleaning_id_item_type",
                schema: "housekeeping",
                table: "cleaning_checklist_items",
                columns: new[] { "tenant_id", "cleaning_id", "item_type" },
                unique: true);

            // --- Row-Level Security (Fase 6, Incremento 2A) --- same
            // pattern as every other tenant-owned table in this schema
            // (InitialCreate). The schema-wide ALTER DEFAULT PRIVILEGES from
            // InitialCreate already granted ihostpro_app SELECT/INSERT/UPDATE/DELETE
            // on this new table automatically — no explicit GRANT needed
            // (unlike cleaning_occurrences/cleaning_audit_log, this table IS
            // meant to be updated in place, so UPDATE/DELETE are kept).
            migrationBuilder.Sql("ALTER TABLE housekeeping.cleaning_checklist_items ENABLE ROW LEVEL SECURITY;");
            migrationBuilder.Sql("ALTER TABLE housekeeping.cleaning_checklist_items FORCE ROW LEVEL SECURITY;");
            migrationBuilder.Sql("""
                CREATE POLICY tenant_isolation ON housekeeping.cleaning_checklist_items
                    USING (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid)
                    WITH CHECK (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "cleaning_checklist_items",
                schema: "housekeeping");
        }
    }
}
