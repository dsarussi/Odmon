using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Odmon.Worker.Migrations
{
    /// <inheritdoc />
    public partial class AddEmailAutomation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "EmailAutomationLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Mailbox = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: false),
                    RuleName = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    InternetMessageId = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    GraphMessageId = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: false),
                    Subject = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    Sender = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: true),
                    ReceivedDateTimeUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    DetectedCourtCaseNumber = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    TargetEmail = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: true),
                    Action = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    IdempotencyKey = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    ErrorMessage = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ProcessedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EmailAutomationLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "EmailAutomationMailboxStates",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Mailbox = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: false),
                    FolderId = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    DeltaLink = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ProcessingFromUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    LastSuccessfulSyncUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EmailAutomationMailboxStates", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_EmailAutomationLogs_CreatedAtUtc",
                table: "EmailAutomationLogs",
                column: "CreatedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_EmailAutomationLogs_IdempotencyKey",
                table: "EmailAutomationLogs",
                column: "IdempotencyKey",
                unique: true,
                filter: "[IdempotencyKey] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_EmailAutomationLogs_Mailbox_GraphMessageId",
                table: "EmailAutomationLogs",
                columns: new[] { "Mailbox", "GraphMessageId" });

            migrationBuilder.CreateIndex(
                name: "IX_EmailAutomationMailboxStates_Mailbox_FolderId",
                table: "EmailAutomationMailboxStates",
                columns: new[] { "Mailbox", "FolderId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "EmailAutomationLogs");

            migrationBuilder.DropTable(
                name: "EmailAutomationMailboxStates");
        }
    }
}
