using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Odmon.Worker.Migrations
{
    /// <inheritdoc />
    public partial class AddEmailAlertDedupsAndSyncRunMetrics : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "EmailAlertDedups",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Fingerprint = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    ExceptionType = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    Source = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    Subject = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: true),
                    FirstSeenUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    LastSeenUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    OccurrenceCount = table.Column<int>(type: "int", nullable: false),
                    LastEmailSentUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    SuppressedCount = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EmailAlertDedups", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SyncRunMetrics",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    RunId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    StartedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CompletedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    DurationMs = table.Column<int>(type: "int", nullable: false),
                    BootstrapCreated = table.Column<int>(type: "int", nullable: false),
                    CoolingFilteredOut = table.Column<int>(type: "int", nullable: false),
                    BootstrapFailed = table.Column<int>(type: "int", nullable: false),
                    Updated = table.Column<int>(type: "int", nullable: false),
                    SkippedNoChange = table.Column<int>(type: "int", nullable: false),
                    SkippedInactive = table.Column<int>(type: "int", nullable: false),
                    SkippedDuplicate = table.Column<int>(type: "int", nullable: false),
                    Failed = table.Column<int>(type: "int", nullable: false),
                    CircuitBreakerTripped = table.Column<bool>(type: "bit", nullable: false),
                    DataSource = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SyncRunMetrics", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_EmailAlertDedups_Fingerprint",
                table: "EmailAlertDedups",
                column: "Fingerprint",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_EmailAlertDedups_LastSeenUtc",
                table: "EmailAlertDedups",
                column: "LastSeenUtc");

            migrationBuilder.CreateIndex(
                name: "IX_SyncRunMetrics_RunId",
                table: "SyncRunMetrics",
                column: "RunId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SyncRunMetrics_StartedAtUtc",
                table: "SyncRunMetrics",
                column: "StartedAtUtc");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "EmailAlertDedups");

            migrationBuilder.DropTable(
                name: "SyncRunMetrics");
        }
    }
}
