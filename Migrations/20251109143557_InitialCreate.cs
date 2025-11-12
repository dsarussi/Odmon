using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Odmon.Worker.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "MondayItemMappings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    TikCounter = table.Column<int>(type: "int", nullable: false),
                    MondayItemId = table.Column<long>(type: "bigint", nullable: false),
                    LastSyncFromOdcanitUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    LastSyncFromMondayUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    OdcanitVersion = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    MondayChecksum = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MondayItemMappings", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SyncLogs",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    Source = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Level = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Message = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Details = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SyncLogs", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MondayItemMappings_MondayItemId",
                table: "MondayItemMappings",
                column: "MondayItemId");

            migrationBuilder.CreateIndex(
                name: "IX_MondayItemMappings_TikCounter",
                table: "MondayItemMappings",
                column: "TikCounter");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MondayItemMappings");

            migrationBuilder.DropTable(
                name: "SyncLogs");
        }
    }
}
