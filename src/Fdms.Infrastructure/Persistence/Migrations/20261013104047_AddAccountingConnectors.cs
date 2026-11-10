using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Fdms.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddAccountingConnectors : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_fdms_integration_access_ClientId",
                table: "fdms_integration_access");

            migrationBuilder.AlterColumn<string>(
                name: "RefreshTokenProtected",
                table: "fdms_integration_connections",
                type: "text",
                maxLength: 4000,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "varchar(2000)",
                oldMaxLength: 2000,
                oldNullable: true)
                .Annotation("MySql:CharSet", "utf8mb4")
                .OldAnnotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AlterColumn<string>(
                name: "AccessTokenProtected",
                table: "fdms_integration_connections",
                type: "text",
                maxLength: 6000,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "varchar(2000)",
                oldMaxLength: 2000,
                oldNullable: true)
                .Annotation("MySql:CharSet", "utf8mb4")
                .OldAnnotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<string>(
                name: "ExtraJson",
                table: "fdms_integration_connections",
                type: "varchar(1000)",
                maxLength: 1000,
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<string>(
                name: "Provider",
                table: "fdms_integration_access",
                type: "varchar(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "zoho")
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateIndex(
                name: "IX_fdms_integration_access_ClientId_Provider",
                table: "fdms_integration_access",
                columns: new[] { "ClientId", "Provider" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_fdms_integration_access_ClientId_Provider",
                table: "fdms_integration_access");

            migrationBuilder.DropColumn(
                name: "ExtraJson",
                table: "fdms_integration_connections");

            migrationBuilder.DropColumn(
                name: "Provider",
                table: "fdms_integration_access");

            migrationBuilder.AlterColumn<string>(
                name: "RefreshTokenProtected",
                table: "fdms_integration_connections",
                type: "varchar(2000)",
                maxLength: 2000,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "text",
                oldMaxLength: 4000,
                oldNullable: true)
                .Annotation("MySql:CharSet", "utf8mb4")
                .OldAnnotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AlterColumn<string>(
                name: "AccessTokenProtected",
                table: "fdms_integration_connections",
                type: "varchar(2000)",
                maxLength: 2000,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "text",
                oldMaxLength: 6000,
                oldNullable: true)
                .Annotation("MySql:CharSet", "utf8mb4")
                .OldAnnotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateIndex(
                name: "IX_fdms_integration_access_ClientId",
                table: "fdms_integration_access",
                column: "ClientId",
                unique: true);
        }
    }
}
