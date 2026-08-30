using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Odmon.Worker.Migrations
{
    /// <inheritdoc />
    public partial class AddEmailFilingResolutionPhantom : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "EmailFilingResolutionRuns",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    EmailFilingDiagnosticId = table.Column<long>(type: "bigint", nullable: false),
                    ExistingAuthorityTargetCount = table.Column<int>(type: "int", nullable: false),
                    PhantomTargetCount = table.Column<int>(type: "int", nullable: false),
                    FinalResolutionClass = table.Column<string>(type: "nvarchar(48)", maxLength: 48, nullable: false),
                    PrimaryEvidenceTypeMask = table.Column<int>(type: "int", nullable: false),
                    SupportingEvidenceTypeMask = table.Column<int>(type: "int", nullable: false),
                    TikPrimaryValueCount = table.Column<int>(type: "int", nullable: false),
                    ClaimPrimaryValueCount = table.Column<int>(type: "int", nullable: false),
                    CourtPrimaryValueCount = table.Column<int>(type: "int", nullable: false),
                    TikCandidateCounterCount = table.Column<int>(type: "int", nullable: false),
                    ClaimCandidateCounterCount = table.Column<int>(type: "int", nullable: false),
                    CourtCandidateCounterCount = table.Column<int>(type: "int", nullable: false),
                    NarrowingApplied = table.Column<bool>(type: "bit", nullable: false),
                    AgreementWithExistingAuthority = table.Column<string>(type: "nvarchar(48)", maxLength: 48, nullable: false),
                    ExtractionDurationMs = table.Column<long>(type: "bigint", nullable: false),
                    PrimaryResolutionDurationMs = table.Column<long>(type: "bigint", nullable: false),
                    SupportingNarrowingDurationMs = table.Column<long>(type: "bigint", nullable: false),
                    TotalPhantomDurationMs = table.Column<long>(type: "bigint", nullable: false),
                    ObserverErrorCategory = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EmailFilingResolutionRuns", x => x.Id);
                    table.ForeignKey(
                        name: "FK_EmailFilingResolutionRuns_EmailFilingDiagnostics_EmailFilingDiagnosticId",
                        column: x => x.EmailFilingDiagnosticId,
                        principalTable: "EmailFilingDiagnostics",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "EmailFilingResolutionTargets",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    ResolutionRunId = table.Column<long>(type: "bigint", nullable: false),
                    TikCounter = table.Column<int>(type: "int", nullable: false),
                    TargetKind = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EmailFilingResolutionTargets", x => x.Id);
                    table.ForeignKey(
                        name: "FK_EmailFilingResolutionTargets_EmailFilingResolutionRuns_ResolutionRunId",
                        column: x => x.ResolutionRunId,
                        principalTable: "EmailFilingResolutionRuns",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_EmailFilingResolutionRuns_AgreementWithExistingAuthority",
                table: "EmailFilingResolutionRuns",
                column: "AgreementWithExistingAuthority");

            migrationBuilder.CreateIndex(
                name: "IX_EmailFilingResolutionRuns_CreatedAtUtc",
                table: "EmailFilingResolutionRuns",
                column: "CreatedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_EmailFilingResolutionRuns_EmailFilingDiagnosticId",
                table: "EmailFilingResolutionRuns",
                column: "EmailFilingDiagnosticId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_EmailFilingResolutionRuns_FinalResolutionClass",
                table: "EmailFilingResolutionRuns",
                column: "FinalResolutionClass");

            migrationBuilder.CreateIndex(
                name: "IX_EmailFilingResolutionTargets_ResolutionRunId_TikCounter_TargetKind",
                table: "EmailFilingResolutionTargets",
                columns: new[] { "ResolutionRunId", "TikCounter", "TargetKind" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_EmailFilingResolutionTargets_TargetKind_TikCounter",
                table: "EmailFilingResolutionTargets",
                columns: new[] { "TargetKind", "TikCounter" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "EmailFilingResolutionTargets");

            migrationBuilder.DropTable(
                name: "EmailFilingResolutionRuns");
        }
    }
}
