using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Fdms.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ProfessionalChartOfAccounts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "Code",
                table: "fdms_ledger_accounts",
                type: "varchar(20)",
                maxLength: 20,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "varchar(10)",
                oldMaxLength: 10)
                .Annotation("MySql:CharSet", "utf8mb4")
                .OldAnnotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<string>(
                name: "Description",
                table: "fdms_ledger_accounts",
                type: "varchar(300)",
                maxLength: 300,
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<bool>(
                name: "IsGroup",
                table: "fdms_ledger_accounts",
                type: "tinyint(1)",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<Guid>(
                name: "ParentId",
                table: "fdms_ledger_accounts",
                type: "char(36)",
                nullable: true,
                collation: "ascii_general_ci");

            migrationBuilder.AddColumn<string>(
                name: "SubType",
                table: "fdms_ledger_accounts",
                type: "varchar(30)",
                maxLength: 30,
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<string>(
                name: "ClassStartsJson",
                table: "fdms_accounting_settings",
                type: "varchar(300)",
                maxLength: 300,
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<string>(
                name: "CodeFormat",
                table: "fdms_accounting_settings",
                type: "varchar(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "9999")
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<int>(
                name: "CodeStep",
                table: "fdms_accounting_settings",
                type: "int",
                nullable: false,
                defaultValue: 10);

            migrationBuilder.AddColumn<bool>(
                name: "EnforceCodeFormat",
                table: "fdms_accounting_settings",
                type: "tinyint(1)",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "FiscalYearStartMonth",
                table: "fdms_accounting_settings",
                type: "int",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.CreateIndex(
                name: "IX_fdms_ledger_accounts_ParentId",
                table: "fdms_ledger_accounts",
                column: "ParentId");

            // the standard accounts get the roles the system now finds them by (so a chart can be renumbered) and their place in the statements
            migrationBuilder.Sql(@"
UPDATE fdms_ledger_accounts SET Role = 'vat_input' WHERE IsSystem = 1 AND Role IS NULL AND Code = '1310';
UPDATE fdms_ledger_accounts SET Role = 'payable' WHERE IsSystem = 1 AND Role IS NULL AND Code = '2010';
UPDATE fdms_ledger_accounts SET Role = 'other_income' WHERE IsSystem = 1 AND Role IS NULL AND Code = '4090';
UPDATE fdms_ledger_accounts SET Role = 'other_expense' WHERE IsSystem = 1 AND Role IS NULL AND Code = '5990';
UPDATE fdms_ledger_accounts SET SubType = 'cash' WHERE SubType IS NULL AND (BankAccountId IS NOT NULL OR (IsSystem = 1 AND Code IN ('1010','1020','1030','1040','1050')));
UPDATE fdms_ledger_accounts SET SubType = 'fixed_asset' WHERE SubType IS NULL AND IsSystem = 1 AND Code IN ('1500','1590');
UPDATE fdms_ledger_accounts SET SubType = 'non_current_liability' WHERE SubType IS NULL AND IsSystem = 1 AND Code = '2300';
UPDATE fdms_ledger_accounts SET SubType = 'other_income' WHERE SubType IS NULL AND IsSystem = 1 AND Code = '4090';
UPDATE fdms_ledger_accounts SET SubType = 'cost_of_sales' WHERE SubType IS NULL AND Type = 'Expense' AND Code LIKE '501%';
UPDATE fdms_ledger_accounts a JOIN fdms_ledger_accounts p ON p.ClientId = a.ClientId AND p.IsSystem = 1 AND p.Role = 'bank' AND p.BankAccountId IS NULL
   SET a.ParentId = p.Id WHERE a.BankAccountId IS NOT NULL AND a.ParentId IS NULL;
");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_fdms_ledger_accounts_ParentId",
                table: "fdms_ledger_accounts");

            migrationBuilder.DropColumn(
                name: "Description",
                table: "fdms_ledger_accounts");

            migrationBuilder.DropColumn(
                name: "IsGroup",
                table: "fdms_ledger_accounts");

            migrationBuilder.DropColumn(
                name: "ParentId",
                table: "fdms_ledger_accounts");

            migrationBuilder.DropColumn(
                name: "SubType",
                table: "fdms_ledger_accounts");

            migrationBuilder.DropColumn(
                name: "ClassStartsJson",
                table: "fdms_accounting_settings");

            migrationBuilder.DropColumn(
                name: "CodeFormat",
                table: "fdms_accounting_settings");

            migrationBuilder.DropColumn(
                name: "CodeStep",
                table: "fdms_accounting_settings");

            migrationBuilder.DropColumn(
                name: "EnforceCodeFormat",
                table: "fdms_accounting_settings");

            migrationBuilder.DropColumn(
                name: "FiscalYearStartMonth",
                table: "fdms_accounting_settings");

            migrationBuilder.AlterColumn<string>(
                name: "Code",
                table: "fdms_ledger_accounts",
                type: "varchar(10)",
                maxLength: 10,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "varchar(20)",
                oldMaxLength: 20)
                .Annotation("MySql:CharSet", "utf8mb4")
                .OldAnnotation("MySql:CharSet", "utf8mb4");
        }
    }
}
