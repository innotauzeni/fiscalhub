using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Fdms.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddIntegrationAccess : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Kind",
                table: "fdms_billinginvoices",
                type: "varchar(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "subscription")
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "fdms_integration_access",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    ClientId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    Status = table.Column<string>(type: "varchar(20)", maxLength: 20, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Uses = table.Column<string>(type: "varchar(100)", maxLength: 100, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Note = table.Column<string>(type: "varchar(1000)", maxLength: 1000, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    RequestedBy = table.Column<string>(type: "varchar(200)", maxLength: 200, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    RequestedAt = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    DecidedBy = table.Column<string>(type: "varchar(200)", maxLength: 200, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    DecidedAt = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    DecisionNote = table.Column<string>(type: "varchar(500)", maxLength: 500, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    InvoiceId = table.Column<Guid>(type: "char(36)", nullable: true, collation: "ascii_general_ci"),
                    SetupFee = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    ActivatedAt = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    SuspendedAt = table.Column<DateTime>(type: "datetime(6)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_fdms_integration_access", x => x.Id);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateIndex(
                name: "IX_fdms_integration_access_ClientId",
                table: "fdms_integration_access",
                column: "ClientId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_fdms_integration_access_Status",
                table: "fdms_integration_access",
                column: "Status");

            // companies already using Zoho Books (connected, or with keys or partner access) keep it: their integration counts as approved
            migrationBuilder.Sql(@"
INSERT INTO fdms_integration_access (Id, ClientId, Status, Uses, Note, RequestedBy, RequestedAt, DecidedBy, DecidedAt, DecisionNote, InvoiceId, SetupFee, ActivatedAt, SuspendedAt)
SELECT UUID(), x.ClientId, 'Active', 'fiscalise,own_api,partners', NULL, 'existing', UTC_TIMESTAMP(), 'migration', UTC_TIMESTAMP(), 'In use before approvals were introduced', NULL, 0, UTC_TIMESTAMP(), NULL
FROM (
    SELECT ClientId FROM fdms_integration_connections WHERE Status <> 'Disconnected'
    UNION SELECT ClientId FROM fdms_bridge_keys WHERE ClientId IS NOT NULL AND RevokedAt IS NULL
    UNION SELECT ClientId FROM fdms_bridge_grants WHERE IsActive = 1
) x
WHERE NOT EXISTS (SELECT 1 FROM fdms_integration_access a WHERE a.ClientId = x.ClientId);");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "fdms_integration_access");

            migrationBuilder.DropColumn(
                name: "Kind",
                table: "fdms_billinginvoices");
        }
    }
}
