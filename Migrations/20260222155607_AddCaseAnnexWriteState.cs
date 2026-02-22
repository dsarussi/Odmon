using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Odmon.Worker.Migrations
{
    /// <inheritdoc />
    public partial class AddCaseAnnexWriteState : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "CaseAnnexWriteState",
                columns: table => new
                {
                    TikCounter = table.Column<int>(type: "int", nullable: false),
                    AccidentStoryAnnexWritten = table.Column<bool>(type: "bit", nullable: false),
                    AccidentStoryAnnexWrittenAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    AccidentStoryAnnexWrittenRunId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CaseAnnexWriteState", x => x.TikCounter);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CaseAnnexWriteState");
        }
    }
}
