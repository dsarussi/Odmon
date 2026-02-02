using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Odmon.Worker.Migrations
{
    /// <inheritdoc />
    public partial class AddHearingNearestSnapshot : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_MondayItemMappings_TikCounter",
                table: "MondayItemMappings");

            migrationBuilder.AddColumn<long>(
                name: "BoardId",
                table: "MondayItemMappings",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<bool>(
                name: "IsTest",
                table: "MondayItemMappings",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "TikNumber",
                table: "MondayItemMappings",
                type: "nvarchar(450)",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "HearingNearestSnapshots",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    TikCounter = table.Column<int>(type: "int", nullable: false),
                    BoardId = table.Column<long>(type: "bigint", nullable: false),
                    MondayItemId = table.Column<long>(type: "bigint", nullable: false),
                    NearestStartDateUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    NearestMeetStatus = table.Column<int>(type: "int", nullable: true),
                    JudgeName = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    City = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    LastSyncedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_HearingNearestSnapshots", x => x.Id);
                });

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

            migrationBuilder.CreateTable(
                name: "NispahAuditLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CorrelationId = table.Column<string>(type: "nvarchar(450)", nullable: false),
                    TikVisualID = table.Column<string>(type: "nvarchar(450)", nullable: false),
                    NispahTypeName = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    InfoLength = table.Column<int>(type: "int", nullable: false),
                    InfoHash = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Status = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Error = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NispahAuditLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "NispahDeduplications",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    TikVisualID = table.Column<string>(type: "nvarchar(450)", nullable: false),
                    NispahTypeName = table.Column<string>(type: "nvarchar(450)", nullable: false),
                    InfoHash = table.Column<string>(type: "nvarchar(450)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NispahDeduplications", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MondayItemMappings_TikCounter",
                table: "MondayItemMappings",
                column: "TikCounter",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MondayItemMappings_TikNumber_BoardId",
                table: "MondayItemMappings",
                columns: new[] { "TikNumber", "BoardId" });

            migrationBuilder.CreateIndex(
                name: "IX_HearingNearestSnapshots_MondayItemId",
                table: "HearingNearestSnapshots",
                column: "MondayItemId");

            migrationBuilder.CreateIndex(
                name: "IX_HearingNearestSnapshots_TikCounter_BoardId",
                table: "HearingNearestSnapshots",
                columns: new[] { "TikCounter", "BoardId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MondayHearingApprovalStates_BoardId_MondayItemId",
                table: "MondayHearingApprovalStates",
                columns: new[] { "BoardId", "MondayItemId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_NispahAuditLogs_CorrelationId",
                table: "NispahAuditLogs",
                column: "CorrelationId");

            migrationBuilder.CreateIndex(
                name: "IX_NispahAuditLogs_TikVisualID_CreatedAtUtc",
                table: "NispahAuditLogs",
                columns: new[] { "TikVisualID", "CreatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_NispahDeduplications_CreatedAtUtc",
                table: "NispahDeduplications",
                column: "CreatedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_NispahDeduplications_TikVisualID_NispahTypeName_InfoHash",
                table: "NispahDeduplications",
                columns: new[] { "TikVisualID", "NispahTypeName", "InfoHash" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "HearingNearestSnapshots");

            migrationBuilder.DropTable(
                name: "MondayHearingApprovalStates");

            migrationBuilder.DropTable(
                name: "NispahAuditLogs");

            migrationBuilder.DropTable(
                name: "NispahDeduplications");

            migrationBuilder.DropIndex(
                name: "IX_MondayItemMappings_TikCounter",
                table: "MondayItemMappings");

            migrationBuilder.DropIndex(
                name: "IX_MondayItemMappings_TikNumber_BoardId",
                table: "MondayItemMappings");

            migrationBuilder.DropColumn(
                name: "BoardId",
                table: "MondayItemMappings");

            migrationBuilder.DropColumn(
                name: "IsTest",
                table: "MondayItemMappings");

            migrationBuilder.DropColumn(
                name: "TikNumber",
                table: "MondayItemMappings");

            migrationBuilder.CreateIndex(
                name: "IX_MondayItemMappings_TikCounter",
                table: "MondayItemMappings",
                column: "TikCounter");
        }
    }
}
