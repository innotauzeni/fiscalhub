using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Fdms.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddGoLiveFee : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "Fee",
                table: "fdms_golive",
                type: "decimal(65,30)",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<Guid>(
                name: "InvoiceId",
                table: "fdms_golive",
                type: "char(36)",
                nullable: true,
                collation: "ascii_general_ci");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Fee",
                table: "fdms_golive");

            migrationBuilder.DropColumn(
                name: "InvoiceId",
                table: "fdms_golive");
        }
    }
}
