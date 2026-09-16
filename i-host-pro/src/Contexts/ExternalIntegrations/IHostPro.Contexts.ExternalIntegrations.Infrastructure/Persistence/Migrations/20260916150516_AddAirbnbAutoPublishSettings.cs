using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IHostPro.Contexts.ExternalIntegrations.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddAirbnbAutoPublishSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "auto_publish_enabled",
                schema: "external_integrations",
                table: "airbnb_email_mailbox_connections",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "auto_publish_not_before_utc",
                schema: "external_integrations",
                table: "airbnb_email_mailbox_connections",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "auto_publish_enabled",
                schema: "external_integrations",
                table: "airbnb_email_mailbox_connections");

            migrationBuilder.DropColumn(
                name: "auto_publish_not_before_utc",
                schema: "external_integrations",
                table: "airbnb_email_mailbox_connections");
        }
    }
}
