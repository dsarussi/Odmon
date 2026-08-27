using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Odmon.Worker.Migrations
{
    /// <inheritdoc />
    public partial class AddEmailFilingV1 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "EmailFilingDedups",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    MessageFingerprint = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    TikCounter = table.Column<int>(type: "int", nullable: false),
                    TikNumber = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Status = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    OdcanitDocCounter = table.Column<int>(type: "int", nullable: true),
                    OdcanitDestPath = table.Column<string>(type: "nvarchar(1024)", maxLength: 1024, nullable: true),
                    ExpectedFileLength = table.Column<long>(type: "bigint", nullable: false),
                    LastErrorCategory = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    FiledAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EmailFilingDedups", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "EmailFilingDiagnostics",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Mailbox = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: false),
                    MessageFingerprint = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    ReceivedDateTimeUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    TikCandidateCount = table.Column<int>(type: "int", nullable: false),
                    ResolvedTikCount = table.Column<int>(type: "int", nullable: false),
                    SuspectNotCaseCount = table.Column<int>(type: "int", nullable: false),
                    CourtCandidateCount = table.Column<int>(type: "int", nullable: false),
                    ResolvedCourtCount = table.Column<int>(type: "int", nullable: false),
                    TargetCount = table.Column<int>(type: "int", nullable: false),
                    DedupHitCount = table.Column<int>(type: "int", nullable: false),
                    ObserverClassifications = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    FinalDecision = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EmailFilingDiagnostics", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "EmailFilingMailboxStates",
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
                    table.PrimaryKey("PK_EmailFilingMailboxStates", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "EmailFilingCandidateDiagnostics",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    EmailFilingDiagnosticId = table.Column<long>(type: "bigint", nullable: false),
                    CandidateType = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    Source = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    Candidate = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    ResolutionStatus = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    ResolvedTikCounter = table.Column<int>(type: "int", nullable: true),
                    ResolvedTikNumber = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EmailFilingCandidateDiagnostics", x => x.Id);
                    table.ForeignKey(
                        name: "FK_EmailFilingCandidateDiagnostics_EmailFilingDiagnostics_EmailFilingDiagnosticId",
                        column: x => x.EmailFilingDiagnosticId,
                        principalTable: "EmailFilingDiagnostics",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "EmailFilingTargetDiagnostics",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    EmailFilingDiagnosticId = table.Column<long>(type: "bigint", nullable: false),
                    TikCounter = table.Column<int>(type: "int", nullable: false),
                    TikNumber = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    RealWriteAllowlisted = table.Column<bool>(type: "bit", nullable: false),
                    DedupResult = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    Decision = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EmailFilingTargetDiagnostics", x => x.Id);
                    table.ForeignKey(
                        name: "FK_EmailFilingTargetDiagnostics_EmailFilingDiagnostics_EmailFilingDiagnosticId",
                        column: x => x.EmailFilingDiagnosticId,
                        principalTable: "EmailFilingDiagnostics",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.AddCheckConstraint(
                name: "CK_MondayItemMappings_TikCounter_Positive",
                table: "MondayItemMappings",
                sql: "[TikCounter] > 0");

            migrationBuilder.CreateIndex(
                name: "IX_EmailFilingCandidateDiagnostics_CandidateType_ResolutionStatus",
                table: "EmailFilingCandidateDiagnostics",
                columns: new[] { "CandidateType", "ResolutionStatus" });

            migrationBuilder.CreateIndex(
                name: "IX_EmailFilingCandidateDiagnostics_EmailFilingDiagnosticId",
                table: "EmailFilingCandidateDiagnostics",
                column: "EmailFilingDiagnosticId");

            migrationBuilder.CreateIndex(
                name: "IX_EmailFilingDedups_MessageFingerprint_TikCounter",
                table: "EmailFilingDedups",
                columns: new[] { "MessageFingerprint", "TikCounter" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_EmailFilingDiagnostics_CreatedAtUtc",
                table: "EmailFilingDiagnostics",
                column: "CreatedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_EmailFilingDiagnostics_FinalDecision",
                table: "EmailFilingDiagnostics",
                column: "FinalDecision");

            migrationBuilder.CreateIndex(
                name: "IX_EmailFilingDiagnostics_MessageFingerprint",
                table: "EmailFilingDiagnostics",
                column: "MessageFingerprint");

            migrationBuilder.CreateIndex(
                name: "IX_EmailFilingMailboxStates_Mailbox_FolderId",
                table: "EmailFilingMailboxStates",
                columns: new[] { "Mailbox", "FolderId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_EmailFilingTargetDiagnostics_Decision",
                table: "EmailFilingTargetDiagnostics",
                column: "Decision");

            migrationBuilder.CreateIndex(
                name: "IX_EmailFilingTargetDiagnostics_EmailFilingDiagnosticId",
                table: "EmailFilingTargetDiagnostics",
                column: "EmailFilingDiagnosticId");

            migrationBuilder.CreateIndex(
                name: "IX_EmailFilingTargetDiagnostics_TikCounter",
                table: "EmailFilingTargetDiagnostics",
                column: "TikCounter");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "EmailFilingCandidateDiagnostics");

            migrationBuilder.DropTable(
                name: "EmailFilingDedups");

            migrationBuilder.DropTable(
                name: "EmailFilingMailboxStates");

            migrationBuilder.DropTable(
                name: "EmailFilingTargetDiagnostics");

            migrationBuilder.DropTable(
                name: "EmailFilingDiagnostics");

            migrationBuilder.DropCheckConstraint(
                name: "CK_MondayItemMappings_TikCounter_Positive",
                table: "MondayItemMappings");
        }
    }
}
