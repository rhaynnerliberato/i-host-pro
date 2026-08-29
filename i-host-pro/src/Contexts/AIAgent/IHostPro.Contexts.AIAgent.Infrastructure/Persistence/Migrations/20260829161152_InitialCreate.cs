using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IHostPro.Contexts.AIAgent.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "ai_agent");

            migrationBuilder.CreateTable(
                name: "agent_interactions",
                schema: "ai_agent",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    agent_session_id = table.Column<Guid>(type: "uuid", nullable: false),
                    inbound_message_id = table.Column<Guid>(type: "uuid", nullable: false),
                    intent = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    language = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: true),
                    confidence = table.Column<decimal>(type: "numeric(5,4)", nullable: true),
                    model_provider = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    model_name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    input_tokens = table.Column<int>(type: "integer", nullable: false),
                    output_tokens = table.Column<int>(type: "integer", nullable: false),
                    started_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    completed_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    outcome = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_agent_interactions", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "agent_sessions",
                schema: "ai_agent",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    conversation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    reservation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    language = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: true),
                    intent = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    confidence = table.Column<decimal>(type: "numeric(5,4)", nullable: true),
                    model_provider = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    model_name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    started_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    last_interaction_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ended_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_agent_sessions", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_agent_interactions_tenant_id_inbound_message_id_unique",
                schema: "ai_agent",
                table: "agent_interactions",
                columns: new[] { "tenant_id", "inbound_message_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_agent_sessions_tenant_id_conversation_id_active_unique",
                schema: "ai_agent",
                table: "agent_sessions",
                columns: new[] { "tenant_id", "conversation_id" },
                unique: true,
                filter: "status = 'Active'");

            // --- Least-privilege grants (Fase 11, Checkpoint 2 — AI Agent
            // Foundation) ---
            //
            // Mirrors every other Bounded Context's own InitialCreate
            // migration exactly: ihostpro_app receives only CONNECT + schema
            // USAGE + the minimum CRUD each table actually needs — never
            // CREATE/ALTER/DROP/TRUNCATE/BYPASSRLS. No DELETE — neither
            // aggregate is ever deleted, only created and updated in place.
            migrationBuilder.Sql("REVOKE ALL ON SCHEMA ai_agent FROM PUBLIC;");
            migrationBuilder.Sql("GRANT USAGE ON SCHEMA ai_agent TO ihostpro_app;");

            migrationBuilder.Sql("GRANT SELECT, INSERT, UPDATE ON ai_agent.agent_sessions TO ihostpro_app;");
            migrationBuilder.Sql("GRANT SELECT, INSERT, UPDATE ON ai_agent.agent_interactions TO ihostpro_app;");

            migrationBuilder.Sql(
                "ALTER DEFAULT PRIVILEGES FOR ROLE ihostpro_migrator IN SCHEMA ai_agent " +
                "GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO ihostpro_app;");

            // --- Row-Level Security (Fase 11, Checkpoint 2) ---
            //
            // Both tables are tenant-owned. Same current_setting(..., true)/
            // NULLIF fail-closed pattern as every other Bounded Context:
            // absence of a resolved tenant yields zero rows visible/writable,
            // never an error. FORCE applies the policy even to the table
            // owner (ihostpro_migrator has no BYPASSRLS).
            migrationBuilder.Sql("ALTER TABLE ai_agent.agent_sessions ENABLE ROW LEVEL SECURITY;");
            migrationBuilder.Sql("ALTER TABLE ai_agent.agent_sessions FORCE ROW LEVEL SECURITY;");
            migrationBuilder.Sql("""
                CREATE POLICY tenant_isolation ON ai_agent.agent_sessions
                    USING (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid)
                    WITH CHECK (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid);
                """);

            migrationBuilder.Sql("ALTER TABLE ai_agent.agent_interactions ENABLE ROW LEVEL SECURITY;");
            migrationBuilder.Sql("ALTER TABLE ai_agent.agent_interactions FORCE ROW LEVEL SECURITY;");
            migrationBuilder.Sql("""
                CREATE POLICY tenant_isolation ON ai_agent.agent_interactions
                    USING (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid)
                    WITH CHECK (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Schema-level grant cleanup only — mirrors every other Bounded
            // Context's InitialCreate Down() exactly. The `ai_agent` schema
            // itself is deliberately left in place, owned by
            // ihostpro_migrator regardless of this migration's lifecycle.
            migrationBuilder.Sql(
                "ALTER DEFAULT PRIVILEGES FOR ROLE ihostpro_migrator IN SCHEMA ai_agent " +
                "REVOKE SELECT, INSERT, UPDATE, DELETE ON TABLES FROM ihostpro_app;");
            migrationBuilder.Sql("REVOKE ALL ON ALL TABLES IN SCHEMA ai_agent FROM ihostpro_app;");
            migrationBuilder.Sql("REVOKE USAGE ON SCHEMA ai_agent FROM ihostpro_app;");

            migrationBuilder.DropTable(
                name: "agent_interactions",
                schema: "ai_agent");

            migrationBuilder.DropTable(
                name: "agent_sessions",
                schema: "ai_agent");
        }
    }
}
