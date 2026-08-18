using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IHostPro.Contexts.Housekeeping.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddWorkflowOrchestrationCommandSupport : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameIndex(
                name: "IX_cleanings_tenant_id_reservation_id",
                schema: "housekeeping",
                table: "cleanings",
                newName: "ix_cleanings_tenant_id_reservation_id");

            migrationBuilder.AddColumn<bool>(
                name: "is_cancelled",
                schema: "housekeeping",
                table: "reservation_projection",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AlterColumn<Guid>(
                name: "created_by_user_id",
                schema: "housekeeping",
                table: "cleanings",
                type: "uuid",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uuid");

            migrationBuilder.AlterColumn<Guid>(
                name: "actor_user_id",
                schema: "housekeeping",
                table: "cleaning_audit_log",
                type: "uuid",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uuid");

            migrationBuilder.CreateIndex(
                name: "ix_cleanings_tenant_id_reservation_id_automated_unique",
                schema: "housekeeping",
                table: "cleanings",
                columns: new[] { "tenant_id", "reservation_id" },
                unique: true,
                filter: "\"created_by_user_id\" IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_cleanings_tenant_id_reservation_id_automated_unique",
                schema: "housekeeping",
                table: "cleanings");

            migrationBuilder.DropColumn(
                name: "is_cancelled",
                schema: "housekeeping",
                table: "reservation_projection");

            migrationBuilder.RenameIndex(
                name: "ix_cleanings_tenant_id_reservation_id",
                schema: "housekeeping",
                table: "cleanings",
                newName: "IX_cleanings_tenant_id_reservation_id");

            migrationBuilder.AlterColumn<Guid>(
                name: "created_by_user_id",
                schema: "housekeeping",
                table: "cleanings",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);

            migrationBuilder.AlterColumn<Guid>(
                name: "actor_user_id",
                schema: "housekeeping",
                table: "cleaning_audit_log",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);
        }
    }
}
