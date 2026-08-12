using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IHostPro.Contexts.Housekeeping.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddCleaningScheduledAtUtc : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "scheduled_at_utc",
                schema: "housekeeping",
                table: "cleanings",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_cleanings_tenant_id_assigned_housekeeper_user_id_scheduled_~",
                schema: "housekeeping",
                table: "cleanings",
                columns: new[] { "tenant_id", "assigned_housekeeper_user_id", "scheduled_at_utc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_cleanings_tenant_id_assigned_housekeeper_user_id_scheduled_~",
                schema: "housekeeping",
                table: "cleanings");

            migrationBuilder.DropColumn(
                name: "scheduled_at_utc",
                schema: "housekeeping",
                table: "cleanings");
        }
    }
}
