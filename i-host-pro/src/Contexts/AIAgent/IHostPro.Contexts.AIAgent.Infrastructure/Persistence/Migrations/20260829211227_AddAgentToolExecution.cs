using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IHostPro.Contexts.AIAgent.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddAgentToolExecution : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "agent_tool_executions",
                schema: "ai_agent",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    agent_interaction_id = table.Column<Guid>(type: "uuid", nullable: false),
                    tool_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    started_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    completed_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    outcome = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    duration_ms = table.Column<long>(type: "bigint", nullable: true),
                    failure_code = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_agent_tool_executions", x => x.id);
                    table.ForeignKey(
                        name: "fk_agent_tool_executions_agent_interactions",
                        column: x => x.agent_interaction_id,
                        principalSchema: "ai_agent",
                        principalTable: "agent_interactions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_agent_tool_executions_agent_interaction_id",
                schema: "ai_agent",
                table: "agent_tool_executions",
                column: "agent_interaction_id");

            migrationBuilder.CreateIndex(
                name: "ix_agent_tool_executions_tenant_id_agent_interaction_id",
                schema: "ai_agent",
                table: "agent_tool_executions",
                columns: new[] { "tenant_id", "agent_interaction_id" });

            // --- Least-privilege grants (Fase 11, Checkpoint 3 — Read Tools
            // & Context Builder) ---
            //
            // Mirrors InitialCreate's own grant pattern exactly. No DELETE —
            // the aggregate is never deleted, only created and updated in
            // place.
            migrationBuilder.Sql("GRANT SELECT, INSERT, UPDATE ON ai_agent.agent_tool_executions TO ihostpro_app;");

            // --- Row-Level Security (Fase 11, Checkpoint 3) ---
            //
            // Same current_setting(..., true)/NULLIF fail-closed pattern as
            // every other table in this schema. FORCE applies the policy
            // even to the table owner (ihostpro_migrator has no BYPASSRLS).
            migrationBuilder.Sql("ALTER TABLE ai_agent.agent_tool_executions ENABLE ROW LEVEL SECURITY;");
            migrationBuilder.Sql("ALTER TABLE ai_agent.agent_tool_executions FORCE ROW LEVEL SECURITY;");
            migrationBuilder.Sql("""
                CREATE POLICY tenant_isolation ON ai_agent.agent_tool_executions
                    USING (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid)
                    WITH CHECK (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("REVOKE ALL ON ai_agent.agent_tool_executions FROM ihostpro_app;");

            migrationBuilder.DropTable(
                name: "agent_tool_executions",
                schema: "ai_agent");
        }
    }
}
