using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Odmon.Worker.Migrations
{
    /// <inheritdoc />
    public partial class AddNispahWriteLog : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "NispahWriteLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    TikCounter = table.Column<int>(type: "int", nullable: false),
                    TikVisualId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    NispahType = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    SourceKind = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    SourceItemId = table.Column<long>(type: "bigint", nullable: false),
                    SourceAssetId = table.Column<long>(type: "bigint", nullable: true),
                    InfoHash = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    Failed = table.Column<bool>(type: "bit", nullable: false),
                    ErrorMessage = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NispahWriteLogs", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_NispahWriteLogs_CreatedAtUtc",
                table: "NispahWriteLogs",
                column: "CreatedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_NispahWriteLogs_TikCounter_NispahType_SourceItemId_InfoHash",
                table: "NispahWriteLogs",
                columns: new[] { "TikCounter", "NispahType", "SourceItemId", "InfoHash" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "NispahWriteLogs");
        }
    }
}
