using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Fdms.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddUsageAndAddons : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "PricePerExtraReceipt",
                table: "fdms_plans",
                type: "decimal(18,4)",
                precision: 18,
                scale: 4,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<int>(
                name: "PaidBranches",
                table: "fdms_clients",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "PaidDevices",
                table: "fdms_clients",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "PaidUsers",
                table: "fdms_clients",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTime>(
                name: "UsageBilledUntil",
                table: "fdms_clients",
                type: "datetime(6)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AddonsJson",
                table: "fdms_billinginvoices",
                type: "varchar(200)",
                maxLength: 200,
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "PricePerExtraReceipt",
                table: "fdms_plans");

            migrationBuilder.DropColumn(
                name: "PaidBranches",
                table: "fdms_clients");

            migrationBuilder.DropColumn(
                name: "PaidDevices",
                table: "fdms_clients");

            migrationBuilder.DropColumn(
                name: "PaidUsers",
                table: "fdms_clients");

            migrationBuilder.DropColumn(
                name: "UsageBilledUntil",
                table: "fdms_clients");

            migrationBuilder.DropColumn(
                name: "AddonsJson",
                table: "fdms_billinginvoices");
        }
    }
}
