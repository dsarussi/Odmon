using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Odmon.Worker.Migrations
{
    /// <inheritdoc />
    public partial class AddVoicenterTracking : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "VoicenterApiRequestLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    WeekStartUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    EndpointType = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    CallId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    HttpStatus = table.Column<int>(type: "int", nullable: true),
                    Success = table.Column<bool>(type: "bit", nullable: false),
                    QuotaExceeded = table.Column<bool>(type: "bit", nullable: false),
                    ErrorMessage = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    CorrelationId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_VoicenterApiRequestLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "VoicenterCallProcessingStates",
                columns: table => new
                {
                    CallId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    FirstSeenUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    LastSeenUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    LastCheckedUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    Status = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    Attempts = table.Column<int>(type: "int", nullable: false),
                    TikCounter = table.Column<int>(type: "int", nullable: true),
                    TikVisualId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    LastError = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_VoicenterCallProcessingStates", x => x.CallId);
                });

            migrationBuilder.CreateTable(
                name: "VoicenterQuotaWarningStates",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    WeekStartUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    EndpointType = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    WarningSentAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    RequestCountAtWarning = table.Column<int>(type: "int", nullable: false),
                    Threshold = table.Column<int>(type: "int", nullable: false),
                    HardLimit = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_VoicenterQuotaWarningStates", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_VoicenterApiRequestLogs_CallId",
                table: "VoicenterApiRequestLogs",
                column: "CallId");

            migrationBuilder.CreateIndex(
                name: "IX_VoicenterApiRequestLogs_CreatedAtUtc",
                table: "VoicenterApiRequestLogs",
                column: "CreatedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_VoicenterApiRequestLogs_EndpointType_WeekStartUtc",
                table: "VoicenterApiRequestLogs",
                columns: new[] { "EndpointType", "WeekStartUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_VoicenterApiRequestLogs_WeekStartUtc",
                table: "VoicenterApiRequestLogs",
                column: "WeekStartUtc");

            migrationBuilder.CreateIndex(
                name: "IX_VoicenterCallProcessingStates_LastSeenUtc",
                table: "VoicenterCallProcessingStates",
                column: "LastSeenUtc");

            migrationBuilder.CreateIndex(
                name: "IX_VoicenterCallProcessingStates_Status",
                table: "VoicenterCallProcessingStates",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "UX_VoicenterQuotaWarningStates_Week_Endpoint",
                table: "VoicenterQuotaWarningStates",
                columns: new[] { "WeekStartUtc", "EndpointType" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "VoicenterApiRequestLogs");

            migrationBuilder.DropTable(
                name: "VoicenterCallProcessingStates");

            migrationBuilder.DropTable(
                name: "VoicenterQuotaWarningStates");
        }
    }
}
