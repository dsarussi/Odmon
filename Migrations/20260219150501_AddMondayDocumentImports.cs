using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Odmon.Worker.Migrations
{
    /// <inheritdoc />
    public partial class AddMondayDocumentImports : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "MondayDocumentImports",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false, defaultValueSql: "SYSUTCDATETIME()"),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false, defaultValueSql: "SYSUTCDATETIME()"),
                    TikCounter = table.Column<int>(type: "int", nullable: true),
                    TikVisualID = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    MondayQuestionnaireItemId = table.Column<long>(type: "bigint", nullable: false),
                    LinkedCaseItemId = table.Column<long>(type: "bigint", nullable: true),
                    ColumnId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    AssetId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    OriginalFileName = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: false),
                    FileSizeBytes = table.Column<long>(type: "bigint", nullable: false),
                    InboxFilePath = table.Column<string>(type: "nvarchar(1024)", maxLength: 1024, nullable: true),
                    OdcanitDocCounter = table.Column<int>(type: "int", nullable: true),
                    OdcanitDestPath = table.Column<string>(type: "nvarchar(1024)", maxLength: 1024, nullable: true),
                    Status = table.Column<int>(type: "int", nullable: false),
                    ErrorMessage = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    RetryCount = table.Column<int>(type: "int", nullable: false),
                    LastAttemptAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    AlertSent = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MondayDocumentImports", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MondayDocumentImports_MondayQuestionnaireItemId_ColumnId_AssetId",
                table: "MondayDocumentImports",
                columns: new[] { "MondayQuestionnaireItemId", "ColumnId", "AssetId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MondayDocumentImports_Status",
                table: "MondayDocumentImports",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_MondayDocumentImports_TikCounter",
                table: "MondayDocumentImports",
                column: "TikCounter");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MondayDocumentImports");
        }
    }
}
