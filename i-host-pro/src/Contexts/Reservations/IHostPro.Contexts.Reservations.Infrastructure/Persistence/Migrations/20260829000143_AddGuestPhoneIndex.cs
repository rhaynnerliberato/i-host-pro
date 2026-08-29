using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IHostPro.Contexts.Reservations.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddGuestPhoneIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_reservations_tenant_id_guest_phone",
                schema: "reservations",
                table: "reservations",
                columns: new[] { "tenant_id", "guest_phone" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_reservations_tenant_id_guest_phone",
                schema: "reservations",
                table: "reservations");
        }
    }
}
