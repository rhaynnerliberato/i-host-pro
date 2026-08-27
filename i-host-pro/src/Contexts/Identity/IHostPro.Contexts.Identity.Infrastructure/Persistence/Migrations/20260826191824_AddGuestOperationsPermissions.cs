using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace IHostPro.Contexts.Identity.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddGuestOperationsPermissions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.InsertData(
                schema: "identity",
                table: "permissions",
                columns: new[] { "code", "action", "resource", "scope" },
                values: new object[,]
                {
                    { "GUEST_OPERATIONS:MANAGE", "MANAGE", "GUEST_OPERATIONS", null },
                    { "GUEST_OPERATIONS:READ", "READ", "GUEST_OPERATIONS", null }
                });

            migrationBuilder.InsertData(
                schema: "identity",
                table: "role_permissions",
                columns: new[] { "permission_code", "role_code" },
                values: new object[,]
                {
                    { "GUEST_OPERATIONS:MANAGE", "ADMIN" },
                    { "GUEST_OPERATIONS:READ", "ADMIN" }
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DeleteData(
                schema: "identity",
                table: "role_permissions",
                keyColumns: new[] { "permission_code", "role_code" },
                keyValues: new object[] { "GUEST_OPERATIONS:MANAGE", "ADMIN" });

            migrationBuilder.DeleteData(
                schema: "identity",
                table: "role_permissions",
                keyColumns: new[] { "permission_code", "role_code" },
                keyValues: new object[] { "GUEST_OPERATIONS:READ", "ADMIN" });

            migrationBuilder.DeleteData(
                schema: "identity",
                table: "permissions",
                keyColumn: "code",
                keyValue: "GUEST_OPERATIONS:MANAGE");

            migrationBuilder.DeleteData(
                schema: "identity",
                table: "permissions",
                keyColumn: "code",
                keyValue: "GUEST_OPERATIONS:READ");
        }
    }
}
