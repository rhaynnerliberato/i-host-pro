using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IHostPro.Contexts.AIAgent.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddAgentPendingActionAndOutboundMessageId : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "outbound_message_id",
                schema: "ai_agent",
                table: "agent_interactions",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "agent_pending_actions",
                schema: "ai_agent",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    agent_session_id = table.Column<Guid>(type: "uuid", nullable: false),
                    proposed_by_interaction_id = table.Column<Guid>(type: "uuid", nullable: false),
                    tool_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    sanitized_arguments = table.Column<string>(type: "text", nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    confirmed_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    executed_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    cancelled_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_agent_pending_actions", x => x.id);
                    table.ForeignKey(
                        name: "fk_agent_pending_actions_agent_interactions",
                        column: x => x.proposed_by_interaction_id,
                        principalSchema: "ai_agent",
                        principalTable: "agent_interactions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_agent_pending_actions_active_per_session",
                schema: "ai_agent",
                table: "agent_pending_actions",
                columns: new[] { "tenant_id", "agent_session_id" },
                unique: true,
                filter: "status IN ('Proposed', 'Confirmed')");

            migrationBuilder.CreateIndex(
                name: "IX_agent_pending_actions_proposed_by_interaction_id",
                schema: "ai_agent",
                table: "agent_pending_actions",
                column: "proposed_by_interaction_id");

            // --- Least-privilege grants (Fase 11, Checkpoint 4 — Write
            // Tools & Response Delivery) ---
            //
            // Mirrors AddAgentToolExecution's own grant pattern exactly. No
            // DELETE — the aggregate is never deleted, only created and
            // updated in place.
            migrationBuilder.Sql("GRANT SELECT, INSERT, UPDATE ON ai_agent.agent_pending_actions TO ihostpro_app;");

            // --- Row-Level Security (Fase 11, Checkpoint 4) ---
            //
            // Same current_setting(..., true)/NULLIF fail-closed pattern as
            // every other table in this schema. FORCE applies the policy
            // even to the table owner (ihostpro_migrator has no BYPASSRLS).
            migrationBuilder.Sql("ALTER TABLE ai_agent.agent_pending_actions ENABLE ROW LEVEL SECURITY;");
            migrationBuilder.Sql("ALTER TABLE ai_agent.agent_pending_actions FORCE ROW LEVEL SECURITY;");
            migrationBuilder.Sql("""
                CREATE POLICY tenant_isolation ON ai_agent.agent_pending_actions
                    USING (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid)
                    WITH CHECK (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("REVOKE ALL ON ai_agent.agent_pending_actions FROM ihostpro_app;");

            migrationBuilder.DropTable(
                name: "agent_pending_actions",
                schema: "ai_agent");

            migrationBuilder.DropColumn(
                name: "outbound_message_id",
                schema: "ai_agent",
                table: "agent_interactions");
        }
    }
}
