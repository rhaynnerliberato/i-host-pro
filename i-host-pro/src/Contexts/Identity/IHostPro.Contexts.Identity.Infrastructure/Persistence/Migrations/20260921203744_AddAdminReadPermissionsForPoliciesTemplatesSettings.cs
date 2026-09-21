using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace IHostPro.Contexts.Identity.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddAdminReadPermissionsForPoliciesTemplatesSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.InsertData(
                schema: "identity",
                table: "role_permissions",
                columns: new[] { "permission_code", "role_code" },
                values: new object[,]
                {
                    { "POLICIES:READ", "ADMIN" },
                    { "SETTINGS:READ", "ADMIN" },
                    { "TEMPLATES:READ", "ADMIN" }
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DeleteData(
                schema: "identity",
                table: "role_permissions",
                keyColumns: new[] { "permission_code", "role_code" },
                keyValues: new object[] { "POLICIES:READ", "ADMIN" });

            migrationBuilder.DeleteData(
                schema: "identity",
                table: "role_permissions",
                keyColumns: new[] { "permission_code", "role_code" },
                keyValues: new object[] { "SETTINGS:READ", "ADMIN" });

            migrationBuilder.DeleteData(
                schema: "identity",
                table: "role_permissions",
                keyColumns: new[] { "permission_code", "role_code" },
                keyValues: new object[] { "TEMPLATES:READ", "ADMIN" });
        }
    }
}
