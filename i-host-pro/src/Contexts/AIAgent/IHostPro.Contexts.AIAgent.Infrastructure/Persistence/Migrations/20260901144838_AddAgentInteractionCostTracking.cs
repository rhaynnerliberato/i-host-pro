using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IHostPro.Contexts.AIAgent.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddAgentInteractionCostTracking : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "cost_pricing_reference",
                schema: "ai_agent",
                table: "agent_interactions",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "estimated_cost_usd",
                schema: "ai_agent",
                table: "agent_interactions",
                type: "numeric(12,6)",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "cost_pricing_reference",
                schema: "ai_agent",
                table: "agent_interactions");

            migrationBuilder.DropColumn(
                name: "estimated_cost_usd",
                schema: "ai_agent",
                table: "agent_interactions");
        }
    }
}
