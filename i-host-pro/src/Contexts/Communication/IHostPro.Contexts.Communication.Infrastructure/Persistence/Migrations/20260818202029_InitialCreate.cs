using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IHostPro.Contexts.Communication.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "communication");

            migrationBuilder.CreateTable(
                name: "messages",
                schema: "communication",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    reservation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    channel = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    template_key = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    destination_masked = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: true),
                    rendered_content = table.Column<string>(type: "text", nullable: false),
                    idempotency_key = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    sent_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    failed_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    failure_reason = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_messages", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_messages_idempotency_key",
                schema: "communication",
                table: "messages",
                column: "idempotency_key",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_messages_tenant_id_reservation_id",
                schema: "communication",
                table: "messages",
                columns: new[] { "tenant_id", "reservation_id" });

            // --- Least-privilege grants (Fase 9, Checkpoint 1) ---
            //
            // Mirrors every other Bounded Context's own InitialCreate
            // migration exactly: ihostpro_app receives only CONNECT + schema
            // USAGE + the minimum CRUD `messages` actually needs — never
            // CREATE/ALTER/DROP/TRUNCATE/BYPASSRLS.
            migrationBuilder.Sql("REVOKE ALL ON SCHEMA communication FROM PUBLIC;");
            migrationBuilder.Sql("GRANT USAGE ON SCHEMA communication TO ihostpro_app;");

            migrationBuilder.Sql("GRANT SELECT, INSERT, UPDATE ON communication.messages TO ihostpro_app;");

            migrationBuilder.Sql(
                "ALTER DEFAULT PRIVILEGES FOR ROLE ihostpro_migrator IN SCHEMA communication " +
                "GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO ihostpro_app;");

            // --- Row-Level Security (Fase 9, Checkpoint 1) ---
            //
            // messages is tenant-owned. Same current_setting(..., true)/
            // NULLIF fail-closed pattern as every other Bounded Context:
            // absence of a resolved tenant yields zero rows visible/
            // writable, never an error. FORCE applies the policy even to
            // the table owner (ihostpro_migrator has no BYPASSRLS —
            // Architecture Principles, Section 10).
            migrationBuilder.Sql("ALTER TABLE communication.messages ENABLE ROW LEVEL SECURITY;");
            migrationBuilder.Sql("ALTER TABLE communication.messages FORCE ROW LEVEL SECURITY;");
            migrationBuilder.Sql("""
                CREATE POLICY tenant_isolation ON communication.messages
                    USING (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid)
                    WITH CHECK (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Schema-level grant cleanup only — mirrors every other Bounded
            // Context's InitialCreate Down() exactly. The `communication`
            // schema itself is deliberately left in place, owned by
            // ihostpro_migrator regardless of this migration's lifecycle.
            migrationBuilder.Sql(
                "ALTER DEFAULT PRIVILEGES FOR ROLE ihostpro_migrator IN SCHEMA communication " +
                "REVOKE SELECT, INSERT, UPDATE, DELETE ON TABLES FROM ihostpro_app;");
            migrationBuilder.Sql("REVOKE ALL ON ALL TABLES IN SCHEMA communication FROM ihostpro_app;");
            migrationBuilder.Sql("REVOKE USAGE ON SCHEMA communication FROM ihostpro_app;");

            migrationBuilder.DropTable(
                name: "messages",
                schema: "communication");
        }
    }
}
