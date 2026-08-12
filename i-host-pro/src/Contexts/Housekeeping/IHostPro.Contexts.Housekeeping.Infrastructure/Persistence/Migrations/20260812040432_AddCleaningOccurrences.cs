using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IHostPro.Contexts.Housekeeping.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddCleaningOccurrences : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "cleaning_occurrences",
                schema: "housekeeping",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    cleaning_id = table.Column<Guid>(type: "uuid", nullable: false),
                    type = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    description = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    registered_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    registered_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_cleaning_occurrences", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_cleaning_occurrences_tenant_id_cleaning_id_registered_at_utc",
                schema: "housekeeping",
                table: "cleaning_occurrences",
                columns: new[] { "tenant_id", "cleaning_id", "registered_at_utc" });

            // The schema-wide ALTER DEFAULT PRIVILEGES from InitialCreate
            // already granted ihostpro_app SELECT/INSERT/UPDATE/DELETE on
            // this new table automatically — revoke UPDATE/DELETE here to
            // enforce the same append-only convention as
            // housekeeping.cleaning_audit_log (nothing in this Bounded
            // Context ever updates or deletes an occurrence).
            migrationBuilder.Sql("REVOKE UPDATE, DELETE ON housekeeping.cleaning_occurrences FROM ihostpro_app;");

            // --- Row-Level Security (Fase 6, Incremento 2A) --- same
            // pattern as every other tenant-owned table in this schema
            // (InitialCreate).
            migrationBuilder.Sql("ALTER TABLE housekeeping.cleaning_occurrences ENABLE ROW LEVEL SECURITY;");
            migrationBuilder.Sql("ALTER TABLE housekeeping.cleaning_occurrences FORCE ROW LEVEL SECURITY;");
            migrationBuilder.Sql("""
                CREATE POLICY tenant_isolation ON housekeeping.cleaning_occurrences
                    USING (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid)
                    WITH CHECK (tenant_id = NULLIF(current_setting('app.tenant_id', true), '')::uuid);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "cleaning_occurrences",
                schema: "housekeeping");
        }
    }
}
