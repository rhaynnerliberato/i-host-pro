using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IHostPro.Contexts.Communication.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddMessageProviderMessageId : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "provider_message_id",
                schema: "communication",
                table: "messages",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_messages_tenant_id_provider_message_id",
                schema: "communication",
                table: "messages",
                columns: new[] { "tenant_id", "provider_message_id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_messages_tenant_id_provider_message_id",
                schema: "communication",
                table: "messages");

            migrationBuilder.DropColumn(
                name: "provider_message_id",
                schema: "communication",
                table: "messages");
        }
    }
}
