using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IHostPro.Contexts.Identity.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddIntegrationsManagePermission : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.InsertData(
                schema: "identity",
                table: "permissions",
                columns: new[] { "code", "action", "resource", "scope" },
                values: new object[] { "INTEGRATIONS:MANAGE", "MANAGE", "INTEGRATIONS", null });

            migrationBuilder.InsertData(
                schema: "identity",
                table: "role_permissions",
                columns: new[] { "permission_code", "role_code" },
                values: new object[] { "INTEGRATIONS:MANAGE", "ADMIN" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DeleteData(
                schema: "identity",
                table: "role_permissions",
                keyColumns: new[] { "permission_code", "role_code" },
                keyValues: new object[] { "INTEGRATIONS:MANAGE", "ADMIN" });

            migrationBuilder.DeleteData(
                schema: "identity",
                table: "permissions",
                keyColumn: "code",
                keyValue: "INTEGRATIONS:MANAGE");
        }
    }
}
