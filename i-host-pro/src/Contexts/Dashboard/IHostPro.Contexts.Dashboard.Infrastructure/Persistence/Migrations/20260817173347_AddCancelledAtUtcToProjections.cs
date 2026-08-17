using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IHostPro.Contexts.Dashboard.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddCancelledAtUtcToProjections : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "cancelled_at_utc",
                schema: "dashboard",
                table: "reservation_projection",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "cancelled_at_utc",
                schema: "dashboard",
                table: "cleaning_projection",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_reservation_projection_tenant_id_cancelled_at_utc",
                schema: "dashboard",
                table: "reservation_projection",
                columns: new[] { "tenant_id", "cancelled_at_utc" });

            migrationBuilder.CreateIndex(
                name: "IX_cleaning_projection_tenant_id_cancelled_at_utc",
                schema: "dashboard",
                table: "cleaning_projection",
                columns: new[] { "tenant_id", "cancelled_at_utc" });

            migrationBuilder.CreateIndex(
                name: "IX_cleaning_projection_tenant_id_completed_at_utc",
                schema: "dashboard",
                table: "cleaning_projection",
                columns: new[] { "tenant_id", "completed_at_utc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_reservation_projection_tenant_id_cancelled_at_utc",
                schema: "dashboard",
                table: "reservation_projection");

            migrationBuilder.DropIndex(
                name: "IX_cleaning_projection_tenant_id_cancelled_at_utc",
                schema: "dashboard",
                table: "cleaning_projection");

            migrationBuilder.DropIndex(
                name: "IX_cleaning_projection_tenant_id_completed_at_utc",
                schema: "dashboard",
                table: "cleaning_projection");

            migrationBuilder.DropColumn(
                name: "cancelled_at_utc",
                schema: "dashboard",
                table: "reservation_projection");

            migrationBuilder.DropColumn(
                name: "cancelled_at_utc",
                schema: "dashboard",
                table: "cleaning_projection");
        }
    }
}
