using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IHostPro.Contexts.Identity.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddSecurityAuditEntryActorId : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "actor_id",
                schema: "identity",
                table: "security_audit_log",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_security_audit_log_tenant_id_actor_id_occurred_at",
                schema: "identity",
                table: "security_audit_log",
                columns: new[] { "tenant_id", "actor_id", "occurred_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_security_audit_log_tenant_id_actor_id_occurred_at",
                schema: "identity",
                table: "security_audit_log");

            migrationBuilder.DropColumn(
                name: "actor_id",
                schema: "identity",
                table: "security_audit_log");
        }
    }
}
