using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Odmon.Worker.Migrations
{
    /// <inheritdoc />
    public partial class AddMondayHearingApprovalStates : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // MondayItemMappings already has BoardId / IsTest / TikNumber in this DB,
            // so do NOT attempt to add columns or change indexes here.

            migrationBuilder.CreateTable(
                name: "MondayHearingApprovalStates",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    BoardId = table.Column<long>(type: "bigint", nullable: false),
                    MondayItemId = table.Column<long>(type: "bigint", nullable: false),
                    TikCounter = table.Column<int>(type: "int", nullable: false),
                    LastKnownStatus = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    FirstDecision = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    LastWriteAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MondayHearingApprovalStates", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MondayHearingApprovalStates_BoardId_MondayItemId",
                table: "MondayHearingApprovalStates",
                columns: new[] { "BoardId", "MondayItemId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MondayHearingApprovalStates");
        }
    }
}
