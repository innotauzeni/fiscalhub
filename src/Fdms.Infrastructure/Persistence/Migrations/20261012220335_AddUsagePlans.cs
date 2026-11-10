using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Fdms.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddUsagePlans : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "PricePerDeviceMonthly",
                table: "fdms_plans",
                type: "decimal(18,2)",
                precision: 18,
                scale: 2,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "PricePerThousandReceipts",
                table: "fdms_plans",
                type: "decimal(18,4)",
                precision: 18,
                scale: 4,
                nullable: false,
                defaultValue: 0m);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "PricePerDeviceMonthly",
                table: "fdms_plans");

            migrationBuilder.DropColumn(
                name: "PricePerThousandReceipts",
                table: "fdms_plans");
        }
    }
}
