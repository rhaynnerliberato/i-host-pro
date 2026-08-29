using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IHostPro.Contexts.Communication.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddConversationAndInboundSupport : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "conversation_id",
                schema: "communication",
                table: "messages",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<string>(
                name: "direction",
                schema: "communication",
                table: "messages",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "Outbound");

            // conversation_id's "00000000-..." default above exists only to
            // satisfy NOT NULL against the 3 pre-existing rows for the
            // instant this ADD COLUMN runs — ConversationBackfillBootstrapStep
            // overwrites every one of them immediately after. Dropped here so
            // the placeholder can never silently satisfy a future INSERT that
            // forgets to set it (Message.Create/CreateInbound always do).
            migrationBuilder.Sql("ALTER TABLE communication.messages ALTER COLUMN conversation_id DROP DEFAULT;");

            migrationBuilder.CreateTable(
                name: "conversations",
                schema: "communication",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    reservation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    channel = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    last_message_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_conversations", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_messages_tenant_id_conversation_id_created_at_utc",
                schema: "communication",
                table: "messages",
                columns: new[] { "tenant_id", "conversation_id", "created_at_utc" });

            migrationBuilder.CreateIndex(
                name: "IX_conversations_tenant_id_reservation_id_channel",
                schema: "communication",
                table: "conversations",
                columns: new[] { "tenant_id", "reservation_id", "channel" },
                unique: true);

            // --- Least-privilege grant (Fase 11, Checkpoint 1) ---
            //
            // InitialCreate's own "ALTER DEFAULT PRIVILEGES FOR ROLE
            // ihostpro_migrator IN SCHEMA communication ... TO ihostpro_app"
            // already auto-grants SELECT/INSERT/UPDATE/DELETE to
            // ihostpro_app on this brand-new table — mirrors
            // PropertyManagement's own AddFrontDeskContact migration
            // precedent exactly. conversations is only ever inserted/updated
            // (RecordMessageAt), never physically deleted.
            migrationBuilder.Sql("REVOKE DELETE ON communication.conversations FROM ihostpro_app;");

            // --- Row-Level Security (Fase 11, Checkpoint 1) ---
            //
            // Same current_setting(..., true)/NULLIF fail-closed pattern as
            // every other tenant-owned table in this schema.
            migrationBuilder.Sql("ALTER TABLE communication.conversations ENABLE ROW LEVEL SECURITY;");
            migrationBuilder.Sql("ALTER TABLE communication.conversations FORCE ROW LEVEL SECURITY;");
            migrationBuilder.Sql("""
                CREATE POLICY tenant_isolation ON communication.conversations
                    USING (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid)
                    WITH CHECK (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid);
                """);

            // The real per-tenant backfill of messages.conversation_id from
            // this checkpoint's placeholder default (00000000-...) runs as
            // IHostPro.MigrationRunner's own ConversationBackfillBootstrapStep
            // (ADR-017 mechanism) immediately after this migration applies —
            // NOT here: communication.messages/conversations both have FORCE
            // ROW LEVEL SECURITY, so a single cross-tenant UPDATE/INSERT
            // inside this migration would see zero rows without app.tenant_id
            // set per tenant (same constraint Fase 10's
            // GuestStayOperationBackfillBootstrapStep already hit and solved
            // the same way — never BYPASSRLS/IgnoreQueryFilters).
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("GRANT DELETE ON communication.conversations TO ihostpro_app;");

            migrationBuilder.DropTable(
                name: "conversations",
                schema: "communication");

            migrationBuilder.DropIndex(
                name: "IX_messages_tenant_id_conversation_id_created_at_utc",
                schema: "communication",
                table: "messages");

            migrationBuilder.DropColumn(
                name: "conversation_id",
                schema: "communication",
                table: "messages");

            migrationBuilder.DropColumn(
                name: "direction",
                schema: "communication",
                table: "messages");
        }
    }
}
