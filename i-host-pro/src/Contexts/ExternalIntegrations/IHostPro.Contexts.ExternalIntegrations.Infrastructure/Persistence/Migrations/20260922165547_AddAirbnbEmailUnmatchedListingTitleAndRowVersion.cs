using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IHostPro.Contexts.ExternalIntegrations.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddAirbnbEmailUnmatchedListingTitleAndRowVersion : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "unmatched_listing_title",
                schema: "external_integrations",
                table: "airbnb_email_message_receipts",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<uint>(
                name: "xmin",
                schema: "external_integrations",
                table: "airbnb_email_message_receipts",
                type: "xid",
                rowVersion: true,
                nullable: false,
                defaultValue: 0u);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "unmatched_listing_title",
                schema: "external_integrations",
                table: "airbnb_email_message_receipts");

            migrationBuilder.DropColumn(
                name: "xmin",
                schema: "external_integrations",
                table: "airbnb_email_message_receipts");
        }
    }
}
