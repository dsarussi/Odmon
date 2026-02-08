using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Odmon.Worker.Migrations
{
    /// <inheritdoc />
    public partial class AddSyncRunLock : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "SyncFailures",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    RunId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    TikCounter = table.Column<int>(type: "int", nullable: false),
                    TikNumber = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    BoardId = table.Column<long>(type: "bigint", nullable: false),
                    Operation = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    ErrorType = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    ErrorMessage = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: false),
                    StackTrace = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true),
                    OccurredAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    RetryAttempts = table.Column<int>(type: "int", nullable: false),
                    Resolved = table.Column<bool>(type: "bit", nullable: false),
                    ResolvedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SyncFailures", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SyncRunLocks",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    LockedByRunId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    LockedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ExpiresAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SyncRunLocks", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SyncFailures_Resolved",
                table: "SyncFailures",
                column: "Resolved");

            migrationBuilder.CreateIndex(
                name: "IX_SyncFailures_RunId",
                table: "SyncFailures",
                column: "RunId");

            migrationBuilder.CreateIndex(
                name: "IX_SyncFailures_TikCounter_OccurredAtUtc",
                table: "SyncFailures",
                columns: new[] { "TikCounter", "OccurredAtUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SyncFailures");

            migrationBuilder.DropTable(
                name: "SyncRunLocks");
        }
    }
}
