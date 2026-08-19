using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IHostPro.Contexts.Configuration.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddTemplates : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "templates",
                schema: "configuration",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    key = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    content = table.Column<string>(type: "text", nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_templates", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_templates_tenant_id_key",
                schema: "configuration",
                table: "templates",
                columns: new[] { "tenant_id", "key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_templates_tenant_id_key_is_active",
                schema: "configuration",
                table: "templates",
                columns: new[] { "tenant_id", "key", "is_active" });

            // --- Least-privilege grants (Fase 9, Checkpoint 1) ---
            //
            // Mirrors every other Bounded Context's own migrations exactly:
            // ihostpro_app receives only the CRUD `templates` actually needs
            // — never CREATE/ALTER/DROP/TRUNCATE/BYPASSRLS.
            migrationBuilder.Sql("GRANT SELECT, INSERT, UPDATE, DELETE ON configuration.templates TO ihostpro_app;");

            // --- Row-Level Security (Fase 9, Checkpoint 1) ---
            //
            // templates is tenant-owned. Same current_setting(..., true)/
            // NULLIF fail-closed pattern as every other Bounded Context:
            // absence of a resolved tenant yields zero rows visible/writable,
            // never an error. FORCE applies the policy even to the table
            // owner (ihostpro_migrator has no BYPASSRLS — Architecture
            // Principles, Section 10).
            migrationBuilder.Sql("ALTER TABLE configuration.templates ENABLE ROW LEVEL SECURITY;");
            migrationBuilder.Sql("ALTER TABLE configuration.templates FORCE ROW LEVEL SECURITY;");
            migrationBuilder.Sql("""
                CREATE POLICY tenant_isolation ON configuration.templates
                    USING (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid)
                    WITH CHECK (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("REVOKE ALL ON configuration.templates FROM ihostpro_app;");

            migrationBuilder.DropTable(
                name: "templates",
                schema: "configuration");
        }
    }
}
