using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IHostPro.Contexts.ExternalIntegrations.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "external_integrations");

            migrationBuilder.CreateTable(
                name: "whatsapp_integrations",
                schema: "external_integrations",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    waba_id = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    phone_number_id = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    is_enabled = table.Column<bool>(type: "boolean", nullable: false),
                    access_token_secret_reference = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    app_secret_secret_reference = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    verify_token_secret_reference = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_whatsapp_integrations", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_whatsapp_integrations_tenant_id",
                schema: "external_integrations",
                table: "whatsapp_integrations",
                column: "tenant_id",
                unique: true);

            // --- Least-privilege grants (Fase 9, Checkpoint 2.1) ---
            //
            // Mirrors every other Bounded Context's own InitialCreate
            // migration exactly: ihostpro_app receives only CONNECT + schema
            // USAGE + the minimum CRUD `whatsapp_integrations` actually needs
            // — never CREATE/ALTER/DROP/TRUNCATE/BYPASSRLS.
            migrationBuilder.Sql("REVOKE ALL ON SCHEMA external_integrations FROM PUBLIC;");
            migrationBuilder.Sql("GRANT USAGE ON SCHEMA external_integrations TO ihostpro_app;");

            migrationBuilder.Sql("GRANT SELECT, INSERT, UPDATE ON external_integrations.whatsapp_integrations TO ihostpro_app;");

            migrationBuilder.Sql(
                "ALTER DEFAULT PRIVILEGES FOR ROLE ihostpro_migrator IN SCHEMA external_integrations " +
                "GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO ihostpro_app;");

            // --- Row-Level Security (Fase 9, Checkpoint 2.1) ---
            //
            // whatsapp_integrations is tenant-owned. Same current_setting(...,
            // true)/NULLIF fail-closed pattern as every other Bounded
            // Context: absence of a resolved tenant yields zero rows
            // visible/writable, never an error. FORCE applies the policy even
            // to the table owner (ihostpro_migrator has no BYPASSRLS —
            // Architecture Principles, Section 10).
            migrationBuilder.Sql("ALTER TABLE external_integrations.whatsapp_integrations ENABLE ROW LEVEL SECURITY;");
            migrationBuilder.Sql("ALTER TABLE external_integrations.whatsapp_integrations FORCE ROW LEVEL SECURITY;");
            migrationBuilder.Sql("""
                CREATE POLICY tenant_isolation ON external_integrations.whatsapp_integrations
                    USING (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid)
                    WITH CHECK (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Schema-level grant cleanup only — mirrors every other Bounded
            // Context's InitialCreate Down() exactly. The
            // `external_integrations` schema itself is deliberately left in
            // place, owned by ihostpro_migrator regardless of this
            // migration's lifecycle.
            migrationBuilder.Sql(
                "ALTER DEFAULT PRIVILEGES FOR ROLE ihostpro_migrator IN SCHEMA external_integrations " +
                "REVOKE SELECT, INSERT, UPDATE, DELETE ON TABLES FROM ihostpro_app;");
            migrationBuilder.Sql("REVOKE ALL ON ALL TABLES IN SCHEMA external_integrations FROM ihostpro_app;");
            migrationBuilder.Sql("REVOKE USAGE ON SCHEMA external_integrations FROM ihostpro_app;");

            migrationBuilder.DropTable(
                name: "whatsapp_integrations",
                schema: "external_integrations");
        }
    }
}
