using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IHostPro.Contexts.Communication.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddAdministratorNotificationContact : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "administrator_notification_contacts",
                schema: "communication",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    destination_phone = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_administrator_notification_contacts", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_administrator_notification_contacts_active_per_tenant",
                schema: "communication",
                table: "administrator_notification_contacts",
                column: "tenant_id",
                unique: true,
                filter: "is_active");

            // --- Least-privilege grant (Fase 11, Checkpoint 6) ---
            //
            // InitialCreate's own "ALTER DEFAULT PRIVILEGES FOR ROLE
            // ihostpro_migrator IN SCHEMA communication ... TO ihostpro_app"
            // already auto-grants SELECT/INSERT/UPDATE/DELETE to
            // ihostpro_app on this brand-new table — mirrors
            // AddConversationAndInboundSupport's own precedent exactly.
            // administrator_notification_contacts is only ever
            // inserted/updated (Upsert never deletes; deactivation is a
            // status flip), never physically deleted.
            migrationBuilder.Sql("REVOKE DELETE ON communication.administrator_notification_contacts FROM ihostpro_app;");

            // --- Row-Level Security (Fase 11, Checkpoint 6) ---
            //
            // Same current_setting(..., true)/NULLIF fail-closed pattern as
            // every other tenant-owned table in this schema.
            migrationBuilder.Sql("ALTER TABLE communication.administrator_notification_contacts ENABLE ROW LEVEL SECURITY;");
            migrationBuilder.Sql("ALTER TABLE communication.administrator_notification_contacts FORCE ROW LEVEL SECURITY;");
            migrationBuilder.Sql("""
                CREATE POLICY tenant_isolation ON communication.administrator_notification_contacts
                    USING (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid)
                    WITH CHECK (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("GRANT DELETE ON communication.administrator_notification_contacts TO ihostpro_app;");

            migrationBuilder.DropTable(
                name: "administrator_notification_contacts",
                schema: "communication");
        }
    }
}
