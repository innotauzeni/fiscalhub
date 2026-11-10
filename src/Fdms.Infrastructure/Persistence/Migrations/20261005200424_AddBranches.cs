using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Fdms.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddBranches : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AccessScope",
                table: "fdms_users",
                type: "longtext",
                nullable: false)
                .Annotation("MySql:CharSet", "utf8mb4");
            migrationBuilder.Sql("UPDATE fdms_users SET AccessScope = 'all' WHERE AccessScope = '';");   // existing users keep access to everything

            migrationBuilder.AddColumn<Guid>(
                name: "BranchId",
                table: "fdms_devices",
                type: "char(36)",
                nullable: true,
                collation: "ascii_general_ci");

            migrationBuilder.CreateTable(
                name: "fdms_branches",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    ClientId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    Code = table.Column<string>(type: "varchar(20)", maxLength: 20, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Name = table.Column<string>(type: "varchar(120)", maxLength: 120, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Address = table.Column<string>(type: "varchar(250)", maxLength: 250, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Phone = table.Column<string>(type: "varchar(40)", maxLength: 40, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    IsActive = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime(6)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_fdms_branches", x => x.Id);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "fdms_userdevices",
                columns: table => new
                {
                    UserId = table.Column<string>(type: "varchar(255)", nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    DeviceId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_fdms_userdevices", x => new { x.UserId, x.DeviceId });
                    table.ForeignKey(
                        name: "FK_fdms_userdevices_fdms_devices_DeviceId",
                        column: x => x.DeviceId,
                        principalTable: "fdms_devices",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_fdms_userdevices_fdms_users_UserId",
                        column: x => x.UserId,
                        principalTable: "fdms_users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "fdms_userbranches",
                columns: table => new
                {
                    UserId = table.Column<string>(type: "varchar(255)", nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    BranchId = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_fdms_userbranches", x => new { x.UserId, x.BranchId });
                    table.ForeignKey(
                        name: "FK_fdms_userbranches_fdms_branches_BranchId",
                        column: x => x.BranchId,
                        principalTable: "fdms_branches",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_fdms_userbranches_fdms_users_UserId",
                        column: x => x.UserId,
                        principalTable: "fdms_users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateIndex(
                name: "IX_fdms_devices_BranchId",
                table: "fdms_devices",
                column: "BranchId");

            migrationBuilder.CreateIndex(
                name: "IX_fdms_branches_ClientId_Code",
                table: "fdms_branches",
                columns: new[] { "ClientId", "Code" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_fdms_userbranches_BranchId",
                table: "fdms_userbranches",
                column: "BranchId");

            migrationBuilder.CreateIndex(
                name: "IX_fdms_userdevices_DeviceId",
                table: "fdms_userdevices",
                column: "DeviceId");

            migrationBuilder.AddForeignKey(
                name: "FK_fdms_devices_fdms_branches_BranchId",
                table: "fdms_devices",
                column: "BranchId",
                principalTable: "fdms_branches",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_fdms_devices_fdms_branches_BranchId",
                table: "fdms_devices");

            migrationBuilder.DropTable(
                name: "fdms_userbranches");

            migrationBuilder.DropTable(
                name: "fdms_userdevices");

            migrationBuilder.DropTable(
                name: "fdms_branches");

            migrationBuilder.DropIndex(
                name: "IX_fdms_devices_BranchId",
                table: "fdms_devices");

            migrationBuilder.DropColumn(
                name: "AccessScope",
                table: "fdms_users");

            migrationBuilder.DropColumn(
                name: "BranchId",
                table: "fdms_devices");
        }
    }
}
