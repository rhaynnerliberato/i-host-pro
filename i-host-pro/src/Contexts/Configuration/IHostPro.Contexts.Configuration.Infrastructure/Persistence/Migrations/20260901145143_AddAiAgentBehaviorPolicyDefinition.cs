using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IHostPro.Contexts.Configuration.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddAiAgentBehaviorPolicyDefinition : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.InsertData(
                schema: "configuration",
                table: "policy_definitions",
                columns: new[] { "code", "category", "description", "is_active", "name", "schema_version", "value_type" },
                values: new object[] { "AI_AGENT_BEHAVIOR", "IA", "Instruções de sistema, tom e formalidade do Agente de IA, compostas dinamicamente pelo Context Builder — nunca um prompt fixo no código.", true, "AI Agent Behavior", 1, "Object" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DeleteData(
                schema: "configuration",
                table: "policy_definitions",
                keyColumn: "code",
                keyValue: "AI_AGENT_BEHAVIOR");
        }
    }
}
