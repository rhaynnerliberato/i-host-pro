using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IHostPro.Contexts.PropertyManagement.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "property_management");

            migrationBuilder.CreateTable(
                name: "condominiums",
                schema: "property_management",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    address_zip_code = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    address_street = table.Column<string>(type: "character varying(150)", maxLength: 150, nullable: false),
                    address_number = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    address_complement = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    address_neighborhood = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    address_city = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    address_state = table.Column<string>(type: "character varying(2)", maxLength: 2, nullable: false),
                    address_country = table.Column<string>(type: "character varying(2)", maxLength: 2, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_condominiums", x => x.id);
                    table.UniqueConstraint("AK_condominiums_tenant_id_id", x => new { x.tenant_id, x.id });
                });

            migrationBuilder.CreateTable(
                name: "property_audit_log",
                schema: "property_management",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    actor_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    entity_type = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    aggregate_id = table.Column<Guid>(type: "uuid", nullable: false),
                    action_code = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    changed_fields = table.Column<string>(type: "jsonb", nullable: false),
                    occurred_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_property_audit_log", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "properties",
                schema: "property_management",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    code = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    normalized_code = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    capacity = table.Column<int>(type: "integer", nullable: false),
                    condominium_id = table.Column<Guid>(type: "uuid", nullable: true),
                    address_zip_code = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: true),
                    address_street = table.Column<string>(type: "character varying(150)", maxLength: 150, nullable: true),
                    address_number = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    address_complement = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    address_neighborhood = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    address_city = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    address_state = table.Column<string>(type: "character varying(2)", maxLength: 2, nullable: true),
                    address_country = table.Column<string>(type: "character varying(2)", maxLength: 2, nullable: true),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_properties", x => x.id);
                    table.UniqueConstraint("AK_properties_tenant_id_id", x => new { x.tenant_id, x.id });
                    table.CheckConstraint("ck_properties_effective_address_source", "condominium_id IS NOT NULL OR (\n    address_street IS NOT NULL AND\n    address_number IS NOT NULL AND\n    address_neighborhood IS NOT NULL AND\n    address_city IS NOT NULL AND\n    address_state IS NOT NULL AND\n    address_zip_code IS NOT NULL AND\n    address_country IS NOT NULL\n)");
                    table.ForeignKey(
                        name: "FK_properties_condominiums_tenant_id_condominium_id",
                        columns: x => new { x.tenant_id, x.condominium_id },
                        principalSchema: "property_management",
                        principalTable: "condominiums",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "property_owners",
                schema: "property_management",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    property_id = table.Column<Guid>(type: "uuid", nullable: false),
                    owner_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_property_owners", x => x.id);
                    table.ForeignKey(
                        name: "FK_property_owners_properties_tenant_id_property_id",
                        columns: x => new { x.tenant_id, x.property_id },
                        principalSchema: "property_management",
                        principalTable: "properties",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_condominiums_tenant_id_id",
                schema: "property_management",
                table: "condominiums",
                columns: new[] { "tenant_id", "id" });

            migrationBuilder.CreateIndex(
                name: "IX_properties_tenant_id_condominium_id",
                schema: "property_management",
                table: "properties",
                columns: new[] { "tenant_id", "condominium_id" });

            migrationBuilder.CreateIndex(
                name: "uq_properties_tenant_normalized_code",
                schema: "property_management",
                table: "properties",
                columns: new[] { "tenant_id", "normalized_code" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_property_audit_log_tenant_id_aggregate_id_occurred_at",
                schema: "property_management",
                table: "property_audit_log",
                columns: new[] { "tenant_id", "aggregate_id", "occurred_at" });

            migrationBuilder.CreateIndex(
                name: "IX_property_audit_log_tenant_id_occurred_at",
                schema: "property_management",
                table: "property_audit_log",
                columns: new[] { "tenant_id", "occurred_at" });

            migrationBuilder.CreateIndex(
                name: "IX_property_owners_tenant_id_owner_user_id",
                schema: "property_management",
                table: "property_owners",
                columns: new[] { "tenant_id", "owner_user_id" });

            migrationBuilder.CreateIndex(
                name: "uq_property_owners_tenant_property_owner",
                schema: "property_management",
                table: "property_owners",
                columns: new[] { "tenant_id", "property_id", "owner_user_id" },
                unique: true);

            // --- Least-privilege grants (Checkpoint 1 plan, item 5) ---
            //
            // Mirrors identity's InitialCreate migration exactly: ihostpro_app
            // (IHostPro.Api/IHostPro.Worker) receives only CONNECT + schema
            // USAGE + CRUD on tables — never CREATE/ALTER/DROP/TRUNCATE/
            // BYPASSRLS. ihostpro_migrator (IHostPro.MigrationRunner) owns the
            // schema/tables by having executed this migration. Neither role is
            // created/altered/dropped here — role lifecycle stays exclusively
            // docker/postgres/init/01-create-roles.sh's responsibility.
            //
            // property_audit_log is the one exception: append-only per
            // Checkpoint 0/1 plan item 11 — ihostpro_app gets SELECT/INSERT
            // only, never UPDATE/DELETE, so no application code path (today
            // or in a future checkpoint) can alter or erase an audit row even
            // by accident.
            migrationBuilder.Sql("REVOKE ALL ON SCHEMA property_management FROM PUBLIC;");
            migrationBuilder.Sql("GRANT USAGE ON SCHEMA property_management TO ihostpro_app;");

            migrationBuilder.Sql("""
                GRANT SELECT, INSERT, UPDATE, DELETE ON
                    property_management.condominiums,
                    property_management.properties,
                    property_management.property_owners
                TO ihostpro_app;
                """);
            migrationBuilder.Sql(
                "GRANT SELECT, INSERT ON property_management.property_audit_log TO ihostpro_app;");

            migrationBuilder.Sql(
                "ALTER DEFAULT PRIVILEGES FOR ROLE ihostpro_migrator IN SCHEMA property_management " +
                "GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO ihostpro_app;");

            // --- Row-Level Security (Checkpoint 0/1 plan, item 8/11) ---
            //
            // All four tables in this schema are tenant-owned. Same
            // current_setting(..., true)/NULLIF fail-closed pattern as
            // identity: absence of a resolved tenant yields zero rows
            // visible/writable, never an error. FORCE applies the policy even
            // to the table owner.
            foreach (var tenantOwnedTable in new[] { "condominiums", "properties", "property_owners", "property_audit_log" })
            {
                migrationBuilder.Sql($"ALTER TABLE property_management.{tenantOwnedTable} ENABLE ROW LEVEL SECURITY;");
                migrationBuilder.Sql($"ALTER TABLE property_management.{tenantOwnedTable} FORCE ROW LEVEL SECURITY;");
                migrationBuilder.Sql($"""
                    CREATE POLICY tenant_isolation ON property_management.{tenantOwnedTable}
                        USING (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid)
                        WITH CHECK (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid);
                    """);
            }
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Schema-level grant cleanup only — mirrors identity's InitialCreate
            // Down() exactly. The `property_management` schema itself is
            // deliberately left in place, owned by ihostpro_migrator
            // regardless of this migration's lifecycle.
            migrationBuilder.Sql(
                "ALTER DEFAULT PRIVILEGES FOR ROLE ihostpro_migrator IN SCHEMA property_management " +
                "REVOKE SELECT, INSERT, UPDATE, DELETE ON TABLES FROM ihostpro_app;");
            migrationBuilder.Sql("REVOKE ALL ON ALL TABLES IN SCHEMA property_management FROM ihostpro_app;");
            migrationBuilder.Sql("REVOKE USAGE ON SCHEMA property_management FROM ihostpro_app;");

            migrationBuilder.DropTable(
                name: "property_audit_log",
                schema: "property_management");

            migrationBuilder.DropTable(
                name: "property_owners",
                schema: "property_management");

            migrationBuilder.DropTable(
                name: "properties",
                schema: "property_management");

            migrationBuilder.DropTable(
                name: "condominiums",
                schema: "property_management");
        }
    }
}
