using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Fdms.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddOwnBridgeKeysAndConnectorPlan : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Kind",
                table: "fdms_plans",
                type: "varchar(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "fiscal")
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AlterColumn<Guid>(
                name: "PartnerId",
                table: "fdms_bridge_keys",
                type: "char(36)",
                nullable: true,
                collation: "ascii_general_ci",
                oldClrType: typeof(Guid),
                oldType: "char(36)")
                .OldAnnotation("Relational:Collation", "ascii_general_ci");

            migrationBuilder.AddColumn<bool>(
                name: "CanWrite",
                table: "fdms_bridge_keys",
                type: "tinyint(1)",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<Guid>(
                name: "ClientId",
                table: "fdms_bridge_keys",
                type: "char(36)",
                nullable: true,
                collation: "ascii_general_ci");

            migrationBuilder.AddColumn<string>(
                name: "Modules",
                table: "fdms_bridge_keys",
                type: "varchar(500)",
                maxLength: 500,
                nullable: false,
                defaultValue: "")
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AlterColumn<Guid>(
                name: "PartnerId",
                table: "fdms_bridge_events",
                type: "char(36)",
                nullable: true,
                collation: "ascii_general_ci",
                oldClrType: typeof(Guid),
                oldType: "char(36)")
                .OldAnnotation("Relational:Collation", "ascii_general_ci");

            migrationBuilder.AlterColumn<Guid>(
                name: "PartnerId",
                table: "fdms_bridge_endpoints",
                type: "char(36)",
                nullable: true,
                collation: "ascii_general_ci",
                oldClrType: typeof(Guid),
                oldType: "char(36)")
                .OldAnnotation("Relational:Collation", "ascii_general_ci");

            migrationBuilder.AddColumn<Guid>(
                name: "ClientId",
                table: "fdms_bridge_endpoints",
                type: "char(36)",
                nullable: true,
                collation: "ascii_general_ci");

            migrationBuilder.CreateIndex(
                name: "IX_fdms_bridge_keys_ClientId",
                table: "fdms_bridge_keys",
                column: "ClientId");

            migrationBuilder.CreateIndex(
                name: "IX_fdms_bridge_endpoints_ClientId",
                table: "fdms_bridge_endpoints",
                column: "ClientId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_fdms_bridge_keys_ClientId",
                table: "fdms_bridge_keys");

            migrationBuilder.DropIndex(
                name: "IX_fdms_bridge_endpoints_ClientId",
                table: "fdms_bridge_endpoints");

            migrationBuilder.DropColumn(
                name: "Kind",
                table: "fdms_plans");

            migrationBuilder.DropColumn(
                name: "CanWrite",
                table: "fdms_bridge_keys");

            migrationBuilder.DropColumn(
                name: "ClientId",
                table: "fdms_bridge_keys");

            migrationBuilder.DropColumn(
                name: "Modules",
                table: "fdms_bridge_keys");

            migrationBuilder.DropColumn(
                name: "ClientId",
                table: "fdms_bridge_endpoints");

            migrationBuilder.AlterColumn<Guid>(
                name: "PartnerId",
                table: "fdms_bridge_keys",
                type: "char(36)",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                collation: "ascii_general_ci",
                oldClrType: typeof(Guid),
                oldType: "char(36)",
                oldNullable: true)
                .OldAnnotation("Relational:Collation", "ascii_general_ci");

            migrationBuilder.AlterColumn<Guid>(
                name: "PartnerId",
                table: "fdms_bridge_events",
                type: "char(36)",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                collation: "ascii_general_ci",
                oldClrType: typeof(Guid),
                oldType: "char(36)",
                oldNullable: true)
                .OldAnnotation("Relational:Collation", "ascii_general_ci");

            migrationBuilder.AlterColumn<Guid>(
                name: "PartnerId",
                table: "fdms_bridge_endpoints",
                type: "char(36)",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                collation: "ascii_general_ci",
                oldClrType: typeof(Guid),
                oldType: "char(36)",
                oldNullable: true)
                .OldAnnotation("Relational:Collation", "ascii_general_ci");
        }
    }
}
