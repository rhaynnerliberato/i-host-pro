using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IHostPro.Contexts.Payments.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddPixChargeExpiredAtUtc : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "expired_at_utc",
                schema: "payments",
                table: "pix_charges",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "expired_at_utc",
                schema: "payments",
                table: "pix_charges");
        }
    }
}
