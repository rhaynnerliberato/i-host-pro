using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IHostPro.Contexts.Reservations.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddExternalReservationIdentity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "external_reservation_id",
                schema: "reservations",
                table: "reservations",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            // Every existing row is a pre-CP3.2 reservation — necessarily
            // Manual (CP3.2 mandate §15: "Como todos os registros existentes
            // serão Manual/null: esperado sem conflito"). The default must be
            // the real, EF-mapped enum string ("Manual", via
            // ReservationConfiguration's plain HasConversion<string>() —
            // Enum.ToString(), never the lowercase "manual" wire code
            // ReservationSourceCodeMapper produces for the Integration Event
            // payload) — an empty-string default would leave every existing
            // row holding a value with no matching ReservationSource member,
            // throwing the next time EF materializes it.
            migrationBuilder.AddColumn<string>(
                name: "source",
                schema: "reservations",
                table: "reservations",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "Manual");

            migrationBuilder.CreateIndex(
                name: "IX_reservations_tenant_id_source_external_reservation_id",
                schema: "reservations",
                table: "reservations",
                columns: new[] { "tenant_id", "source", "external_reservation_id" },
                unique: true,
                filter: "external_reservation_id IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_reservations_tenant_id_source_external_reservation_id",
                schema: "reservations",
                table: "reservations");

            migrationBuilder.DropColumn(
                name: "external_reservation_id",
                schema: "reservations",
                table: "reservations");

            migrationBuilder.DropColumn(
                name: "source",
                schema: "reservations",
                table: "reservations");
        }
    }
}
