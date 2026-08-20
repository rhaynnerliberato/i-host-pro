using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IHostPro.Contexts.Communication.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddMessageDeliveryReadTimestamps : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "delivered_at_utc",
                schema: "communication",
                table: "messages",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "read_at_utc",
                schema: "communication",
                table: "messages",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "delivered_at_utc",
                schema: "communication",
                table: "messages");

            migrationBuilder.DropColumn(
                name: "read_at_utc",
                schema: "communication",
                table: "messages");
        }
    }
}
