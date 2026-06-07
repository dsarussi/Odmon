using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Odmon.Worker.Migrations
{
    /// <inheritdoc />
    public partial class AddNetCourtDecisionAlerts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "NetCourtDecisionAlerts",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    DocumentIdentity = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    TikCounter = table.Column<int>(type: "int", nullable: false),
                    TikNumber = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    ClientNumber = table.Column<int>(type: "int", nullable: true),
                    NetCourtCounter = table.Column<long>(type: "bigint", nullable: false),
                    ODDocID = table.Column<long>(type: "bigint", nullable: true),
                    CourtDocumentID = table.Column<long>(type: "bigint", nullable: true),
                    DecisionID = table.Column<long>(type: "bigint", nullable: true),
                    DocType = table.Column<int>(type: "int", nullable: false),
                    Description = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    DecisionDesc = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    DocDate = table.Column<DateTime>(type: "datetime2", nullable: true),
                    tsCreateDate = table.Column<DateTime>(type: "datetime2", nullable: true),
                    IntendedRecipientEmail = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: true),
                    ActualRecipientEmail = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    EmailMode = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    Status = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    AlertQueuedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ErrorMessage = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NetCourtDecisionAlerts", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "NetCourtDecisionAlertState",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false),
                    BaselineCompletedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NetCourtDecisionAlertState", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_NetCourtDecisionAlerts_CreatedAtUtc",
                table: "NetCourtDecisionAlerts",
                column: "CreatedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_NetCourtDecisionAlerts_DocumentIdentity",
                table: "NetCourtDecisionAlerts",
                column: "DocumentIdentity",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_NetCourtDecisionAlerts_Status",
                table: "NetCourtDecisionAlerts",
                column: "Status");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "NetCourtDecisionAlerts");

            migrationBuilder.DropTable(
                name: "NetCourtDecisionAlertState");
        }
    }
}
