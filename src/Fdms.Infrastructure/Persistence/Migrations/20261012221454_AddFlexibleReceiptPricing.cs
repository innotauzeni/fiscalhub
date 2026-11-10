using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Fdms.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddFlexibleReceiptPricing : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "PricePerDeviceYearly",
                table: "fdms_plans",
                type: "decimal(18,2)",
                precision: 18,
                scale: 2,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "ReceiptCapMonthly",
                table: "fdms_plans",
                type: "decimal(18,2)",
                precision: 18,
                scale: 2,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "ReceiptMinimumMonthly",
                table: "fdms_plans",
                type: "decimal(18,2)",
                precision: 18,
                scale: 2,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<string>(
                name: "ReceiptPricing",
                table: "fdms_plans",
                type: "varchar(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "flat")
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<string>(
                name: "ReceiptTiersJson",
                table: "fdms_plans",
                type: "varchar(2000)",
                maxLength: 2000,
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");
            // plans that already charged per 1,000 receipts keep doing so
            migrationBuilder.Sql("UPDATE fdms_plans SET ReceiptPricing = 'metered' WHERE PricePerThousandReceipts > 0;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "PricePerDeviceYearly",
                table: "fdms_plans");

            migrationBuilder.DropColumn(
                name: "ReceiptCapMonthly",
                table: "fdms_plans");

            migrationBuilder.DropColumn(
                name: "ReceiptMinimumMonthly",
                table: "fdms_plans");

            migrationBuilder.DropColumn(
                name: "ReceiptPricing",
                table: "fdms_plans");

            migrationBuilder.DropColumn(
                name: "ReceiptTiersJson",
                table: "fdms_plans");
        }
    }
}
