using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IHostPro.Contexts.Payments.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "payments");

            migrationBuilder.CreateTable(
                name: "pix_charges",
                schema: "payments",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    late_checkout_request_id = table.Column<Guid>(type: "uuid", nullable: false),
                    reservation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    amount = table.Column<decimal>(type: "numeric(12,2)", nullable: false),
                    currency_code = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    provider_charge_id = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    qr_code_payload = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    idempotency_key = table.Column<Guid>(type: "uuid", nullable: false),
                    expires_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    confirmed_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    failed_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_pix_charges", x => x.id);
                    table.UniqueConstraint("AK_pix_charges_tenant_id_id", x => new { x.tenant_id, x.id });
                });

            migrationBuilder.CreateIndex(
                name: "ix_pix_charges_tenant_id_late_checkout_request_id_active_unique",
                schema: "payments",
                table: "pix_charges",
                columns: new[] { "tenant_id", "late_checkout_request_id" },
                unique: true,
                filter: "status = 'Pending'");

            // --- Least-privilege grants (Fase 10, Checkpoint 5 — PIX/Payment
            // Deterministic Foundation) ---
            //
            // Mirrors every other Bounded Context's own InitialCreate
            // migration exactly: ihostpro_app receives only CONNECT + schema
            // USAGE + the minimum CRUD pix_charges actually needs — never
            // CREATE/ALTER/DROP/TRUNCATE/BYPASSRLS. No DELETE — a PixCharge
            // is never deleted, only created and updated in place.
            migrationBuilder.Sql("REVOKE ALL ON SCHEMA payments FROM PUBLIC;");
            migrationBuilder.Sql("GRANT USAGE ON SCHEMA payments TO ihostpro_app;");

            migrationBuilder.Sql("GRANT SELECT, INSERT, UPDATE ON payments.pix_charges TO ihostpro_app;");

            migrationBuilder.Sql(
                "ALTER DEFAULT PRIVILEGES FOR ROLE ihostpro_migrator IN SCHEMA payments " +
                "GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO ihostpro_app;");

            // --- Row-Level Security (Fase 10, Checkpoint 5) ---
            //
            // pix_charges is tenant-owned. Same current_setting(..., true)/
            // NULLIF fail-closed pattern as every other Bounded Context:
            // absence of a resolved tenant yields zero rows visible/writable,
            // never an error. FORCE applies the policy even to the table
            // owner (ihostpro_migrator has no BYPASSRLS).
            migrationBuilder.Sql("ALTER TABLE payments.pix_charges ENABLE ROW LEVEL SECURITY;");
            migrationBuilder.Sql("ALTER TABLE payments.pix_charges FORCE ROW LEVEL SECURITY;");
            migrationBuilder.Sql("""
                CREATE POLICY tenant_isolation ON payments.pix_charges
                    USING (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid)
                    WITH CHECK (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Schema-level grant cleanup only — mirrors every other Bounded
            // Context's InitialCreate Down() exactly. The `payments` schema
            // itself is deliberately left in place, owned by
            // ihostpro_migrator regardless of this migration's lifecycle.
            migrationBuilder.Sql(
                "ALTER DEFAULT PRIVILEGES FOR ROLE ihostpro_migrator IN SCHEMA payments " +
                "REVOKE SELECT, INSERT, UPDATE, DELETE ON TABLES FROM ihostpro_app;");
            migrationBuilder.Sql("REVOKE ALL ON ALL TABLES IN SCHEMA payments FROM ihostpro_app;");
            migrationBuilder.Sql("REVOKE USAGE ON SCHEMA payments FROM ihostpro_app;");

            migrationBuilder.DropTable(
                name: "pix_charges",
                schema: "payments");
        }
    }
}
