using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IHostPro.Contexts.AIAgent.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddAgentHumanHandoff : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_agent_sessions_tenant_id_conversation_id_active_unique",
                schema: "ai_agent",
                table: "agent_sessions");

            migrationBuilder.CreateTable(
                name: "agent_human_handoffs",
                schema: "ai_agent",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    agent_session_id = table.Column<Guid>(type: "uuid", nullable: false),
                    reason_code = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    requested_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    notification_attempted_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    notified_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    notification_failure_code = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    resumed_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    resumed_by_actor_id = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_agent_human_handoffs", x => x.id);
                    table.ForeignKey(
                        name: "fk_agent_human_handoffs_agent_sessions",
                        column: x => x.agent_session_id,
                        principalSchema: "ai_agent",
                        principalTable: "agent_sessions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_agent_sessions_tenant_id_conversation_id_active_unique",
                schema: "ai_agent",
                table: "agent_sessions",
                columns: new[] { "tenant_id", "conversation_id" },
                unique: true,
                filter: "status IN ('Active', 'Escalated')");

            migrationBuilder.CreateIndex(
                name: "ix_agent_human_handoffs_active_per_session",
                schema: "ai_agent",
                table: "agent_human_handoffs",
                columns: new[] { "tenant_id", "agent_session_id" },
                unique: true,
                filter: "status IN ('Requested', 'Notified')");

            migrationBuilder.CreateIndex(
                name: "IX_agent_human_handoffs_agent_session_id",
                schema: "ai_agent",
                table: "agent_human_handoffs",
                column: "agent_session_id");

            // --- Least-privilege grant (Fase 11, Checkpoint 6) ---
            //
            // Correction of a mischaracterized finding from Checkpoint 4's
            // own homologation: that record attributed a DELETE-grant drift
            // on ai_agent tables to "a stray manual SQL command." Direct
            // schema inspection during THIS checkpoint (\ddp against the
            // real dev database) found the actual root cause: a standing
            // "ALTER DEFAULT PRIVILEGES ... IN SCHEMA ai_agent GRANT
            // arwd ... TO ihostpro_app" — every NEW table in this schema
            // silently inherits DELETE, contradicting the explicit
            // SELECT/INSERT/UPDATE-only GRANT every prior AIAgent migration
            // (AddAgentToolExecution, AddAgentPendingActionAndOutboundMessageId)
            // already declares. Fixed at the source here — REVOKE the
            // default DELETE grant itself, so no future table in this schema
            // silently inherits it again — then grant this table exactly
            // what it needs, same as every prior one.
            migrationBuilder.Sql("ALTER DEFAULT PRIVILEGES FOR ROLE ihostpro_migrator IN SCHEMA ai_agent REVOKE DELETE ON TABLES FROM ihostpro_app;");
            migrationBuilder.Sql("GRANT SELECT, INSERT, UPDATE ON ai_agent.agent_human_handoffs TO ihostpro_app;");

            // --- Row-Level Security (Fase 11, Checkpoint 6) ---
            //
            // Same current_setting(..., true)/NULLIF fail-closed pattern as
            // every other tenant-owned table in this schema.
            migrationBuilder.Sql("ALTER TABLE ai_agent.agent_human_handoffs ENABLE ROW LEVEL SECURITY;");
            migrationBuilder.Sql("ALTER TABLE ai_agent.agent_human_handoffs FORCE ROW LEVEL SECURITY;");
            migrationBuilder.Sql("""
                CREATE POLICY tenant_isolation ON ai_agent.agent_human_handoffs
                    USING (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid)
                    WITH CHECK (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("REVOKE ALL ON ai_agent.agent_human_handoffs FROM ihostpro_app;");
            migrationBuilder.Sql("ALTER DEFAULT PRIVILEGES FOR ROLE ihostpro_migrator IN SCHEMA ai_agent GRANT DELETE ON TABLES TO ihostpro_app;");

            migrationBuilder.DropTable(
                name: "agent_human_handoffs",
                schema: "ai_agent");

            migrationBuilder.DropIndex(
                name: "ix_agent_sessions_tenant_id_conversation_id_active_unique",
                schema: "ai_agent",
                table: "agent_sessions");

            migrationBuilder.CreateIndex(
                name: "ix_agent_sessions_tenant_id_conversation_id_active_unique",
                schema: "ai_agent",
                table: "agent_sessions",
                columns: new[] { "tenant_id", "conversation_id" },
                unique: true,
                filter: "status = 'Active'");
        }
    }
}
