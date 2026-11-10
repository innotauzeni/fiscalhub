using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Fdms.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class GoLiveZimraTestDocuments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "Status",
                table: "fdms_golive",
                type: "varchar(20)",
                maxLength: 20,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "varchar(12)",
                oldMaxLength: 12)
                .Annotation("MySql:CharSet", "utf8mb4")
                .OldAnnotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<string>(
                name: "LiveActivationKeyProtected",
                table: "fdms_golive",
                type: "varchar(500)",
                maxLength: 500,
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<int>(
                name: "LiveDeviceId",
                table: "fdms_golive",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LiveSerialNo",
                table: "fdms_golive",
                type: "varchar(60)",
                maxLength: 60,
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<DateTime>(
                name: "SentToZimraAt",
                table: "fdms_golive",
                type: "datetime(6)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SentToZimraBy",
                table: "fdms_golive",
                type: "varchar(200)",
                maxLength: 200,
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<string>(
                name: "TestReceiptIdsJson",
                table: "fdms_golive",
                type: "varchar(1000)",
                maxLength: 1000,
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<string>(
                name: "ZimraEmail",
                table: "fdms_golive",
                type: "varchar(300)",
                maxLength: 300,
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "LiveActivationKeyProtected",
                table: "fdms_golive");

            migrationBuilder.DropColumn(
                name: "LiveDeviceId",
                table: "fdms_golive");

            migrationBuilder.DropColumn(
                name: "LiveSerialNo",
                table: "fdms_golive");

            migrationBuilder.DropColumn(
                name: "SentToZimraAt",
                table: "fdms_golive");

            migrationBuilder.DropColumn(
                name: "SentToZimraBy",
                table: "fdms_golive");

            migrationBuilder.DropColumn(
                name: "TestReceiptIdsJson",
                table: "fdms_golive");

            migrationBuilder.DropColumn(
                name: "ZimraEmail",
                table: "fdms_golive");

            migrationBuilder.AlterColumn<string>(
                name: "Status",
                table: "fdms_golive",
                type: "varchar(12)",
                maxLength: 12,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "varchar(20)",
                oldMaxLength: 20)
                .Annotation("MySql:CharSet", "utf8mb4")
                .OldAnnotation("MySql:CharSet", "utf8mb4");
        }
    }
}
