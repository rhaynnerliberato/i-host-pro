using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IHostPro.Contexts.ExternalIntegrations.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddAirbnbEmailOAuthTransactions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "airbnb_email_oauth_transactions",
                schema: "external_integrations",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    state_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    actor_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    protected_pkce_verifier = table.Column<byte[]>(type: "bytea", nullable: false),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    expires_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    consumed_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_airbnb_email_oauth_transactions", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_airbnb_email_oauth_transactions_state_hash",
                schema: "external_integrations",
                table: "airbnb_email_oauth_transactions",
                column: "state_hash",
                unique: true);

            // Deliberately NO "ALTER TABLE ... ENABLE ROW LEVEL SECURITY" here,
            // unlike every other tenant-owned table in this schema (Web OAuth
            // architecture gate, item 13): at oauth/callback time the tenant
            // is not yet known — discovering it via ConsumeByStateHashAsync IS
            // the point of this table — so an RLS policy keyed on
            // app.tenant_id would make every row unreadable at exactly the
            // moment it needs to be read. Safety comes from
            // IAirbnbEmailOAuthTransactionRepository's narrow surface
            // (create-pending / atomic-consume-by-hash only) instead.
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "airbnb_email_oauth_transactions",
                schema: "external_integrations");
        }
    }
}
