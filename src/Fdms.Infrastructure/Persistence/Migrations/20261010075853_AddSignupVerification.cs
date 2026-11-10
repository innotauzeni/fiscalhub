using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Fdms.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddSignupVerification : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "KeepWhenUnpaid",
                table: "fdms_clients",
                type: "tinyint(1)",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "UserName",
                table: "fdms_applications",
                type: "varchar(64)",
                maxLength: 64,
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<int>(
                name: "VerifyAttempts",
                table: "fdms_applications",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "VerifyCodeHash",
                table: "fdms_applications",
                type: "varchar(100)",
                maxLength: 100,
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<DateTime>(
                name: "VerifyExpiresAt",
                table: "fdms_applications",
                type: "datetime(6)",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "VerifySentAt",
                table: "fdms_applications",
                type: "datetime(6)",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "KeepWhenUnpaid",
                table: "fdms_clients");

            migrationBuilder.DropColumn(
                name: "UserName",
                table: "fdms_applications");

            migrationBuilder.DropColumn(
                name: "VerifyAttempts",
                table: "fdms_applications");

            migrationBuilder.DropColumn(
                name: "VerifyCodeHash",
                table: "fdms_applications");

            migrationBuilder.DropColumn(
                name: "VerifyExpiresAt",
                table: "fdms_applications");

            migrationBuilder.DropColumn(
                name: "VerifySentAt",
                table: "fdms_applications");
        }
    }
}
