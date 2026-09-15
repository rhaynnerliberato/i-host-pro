using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IHostPro.Contexts.ExternalIntegrations.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddAirbnbEmailBridgeFoundation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "airbnb_email_mailbox_connections",
                schema: "external_integrations",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    mailbox_address = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: true),
                    home_account_id = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    account_tenant_id = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    is_enabled = table.Column<bool>(type: "boolean", nullable: false),
                    granted_scopes = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    token_cache_blob = table.Column<byte[]>(type: "bytea", nullable: true),
                    authorization_status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    last_authenticated_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_airbnb_email_mailbox_connections", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "airbnb_email_message_receipts",
                schema: "external_integrations",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    graph_message_id = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    internet_message_id = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    received_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    processing_status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    detected_event_type = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    external_reservation_id = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    parser_version = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    processed_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    failure_reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_airbnb_email_message_receipts", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "airbnb_email_sync_states",
                schema: "external_integrations",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    mailbox_connection_id = table.Column<Guid>(type: "uuid", nullable: false),
                    mail_folder_id = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    delta_link = table.Column<string>(type: "text", nullable: true),
                    last_successful_sync_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_attempt_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_error_code = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_airbnb_email_sync_states", x => x.id);
                    table.ForeignKey(
                        name: "FK_airbnb_email_sync_states_airbnb_email_mailbox_connections_m~",
                        column: x => x.mailbox_connection_id,
                        principalSchema: "external_integrations",
                        principalTable: "airbnb_email_mailbox_connections",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_airbnb_email_mailbox_connections_tenant_id",
                schema: "external_integrations",
                table: "airbnb_email_mailbox_connections",
                column: "tenant_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_airbnb_email_message_receipts_tenant_id_graph_message_id",
                schema: "external_integrations",
                table: "airbnb_email_message_receipts",
                columns: new[] { "tenant_id", "graph_message_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_airbnb_email_sync_states_mailbox_connection_id",
                schema: "external_integrations",
                table: "airbnb_email_sync_states",
                column: "mailbox_connection_id");

            migrationBuilder.CreateIndex(
                name: "IX_airbnb_email_sync_states_tenant_id_mailbox_connection_id_ma~",
                schema: "external_integrations",
                table: "airbnb_email_sync_states",
                columns: new[] { "tenant_id", "mailbox_connection_id", "mail_folder_id" },
                unique: true);

            // --- Row-Level Security (mirrors AddAirbnbIntegrationFoundation exactly) ---
            //
            // All three tables are tenant-owned. Same current_setting(...,
            // true)/NULLIF fail-closed pattern, FORCE applied even to the
            // table owner. No explicit GRANT needed: InitialCreate's
            // schema-wide ALTER DEFAULT PRIVILEGES already grants
            // ihostpro_app SELECT/INSERT/UPDATE/DELETE on any new table
            // ihostpro_migrator creates in this schema.
            migrationBuilder.Sql("ALTER TABLE external_integrations.airbnb_email_mailbox_connections ENABLE ROW LEVEL SECURITY;");
            migrationBuilder.Sql("ALTER TABLE external_integrations.airbnb_email_mailbox_connections FORCE ROW LEVEL SECURITY;");
            migrationBuilder.Sql("""
                CREATE POLICY tenant_isolation ON external_integrations.airbnb_email_mailbox_connections
                    USING (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid)
                    WITH CHECK (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid);
                """);

            migrationBuilder.Sql("ALTER TABLE external_integrations.airbnb_email_sync_states ENABLE ROW LEVEL SECURITY;");
            migrationBuilder.Sql("ALTER TABLE external_integrations.airbnb_email_sync_states FORCE ROW LEVEL SECURITY;");
            migrationBuilder.Sql("""
                CREATE POLICY tenant_isolation ON external_integrations.airbnb_email_sync_states
                    USING (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid)
                    WITH CHECK (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid);
                """);

            migrationBuilder.Sql("ALTER TABLE external_integrations.airbnb_email_message_receipts ENABLE ROW LEVEL SECURITY;");
            migrationBuilder.Sql("ALTER TABLE external_integrations.airbnb_email_message_receipts FORCE ROW LEVEL SECURITY;");
            migrationBuilder.Sql("""
                CREATE POLICY tenant_isolation ON external_integrations.airbnb_email_message_receipts
                    USING (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid)
                    WITH CHECK (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "airbnb_email_message_receipts",
                schema: "external_integrations");

            migrationBuilder.DropTable(
                name: "airbnb_email_sync_states",
                schema: "external_integrations");

            migrationBuilder.DropTable(
                name: "airbnb_email_mailbox_connections",
                schema: "external_integrations");
        }
    }
}
