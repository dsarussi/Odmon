using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Odmon.Worker.Migrations
{
    /// <inheritdoc />
    public partial class AddEmailFilingAuthorityReviewTelemetry : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AuthorityDecisionClass",
                table: "EmailFilingResolutionRuns",
                type: "nvarchar(48)",
                maxLength: 48,
                nullable: false,
                defaultValue: "LEGACY_UNKNOWN");

            migrationBuilder.AddColumn<int>(
                name: "DecisiveSupportingEvidenceTypeMask",
                table: "EmailFilingResolutionRuns",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<bool>(
                name: "PreferredClaimUsed",
                table: "EmailFilingResolutionRuns",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "SourceTemplateKind",
                table: "EmailFilingResolutionRuns",
                type: "nvarchar(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "LEGACY_UNKNOWN");

            migrationBuilder.CreateTable(
                name: "EmailFilingResolutionCandidates",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    ResolutionRunId = table.Column<long>(type: "bigint", nullable: false),
                    TikCounter = table.Column<int>(type: "int", nullable: false),
                    PrimaryEvidenceType = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    CandidateStage = table.Column<string>(type: "nvarchar(24)", maxLength: 24, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EmailFilingResolutionCandidates", x => x.Id);
                    table.ForeignKey(
                        name: "FK_EmailFilingResolutionCandidates_EmailFilingResolutionRuns_ResolutionRunId",
                        column: x => x.ResolutionRunId,
                        principalTable: "EmailFilingResolutionRuns",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_EmailFilingResolutionRuns_AuthorityDecisionClass",
                table: "EmailFilingResolutionRuns",
                column: "AuthorityDecisionClass");

            migrationBuilder.CreateIndex(
                name: "IX_EmailFilingResolutionCandidates_PrimaryEvidenceType_TikCounter",
                table: "EmailFilingResolutionCandidates",
                columns: new[] { "PrimaryEvidenceType", "TikCounter" });

            migrationBuilder.CreateIndex(
                name: "IX_EmailFilingResolutionCandidates_ResolutionRunId_TikCounter_PrimaryEvidenceType_CandidateStage",
                table: "EmailFilingResolutionCandidates",
                columns: new[] { "ResolutionRunId", "TikCounter", "PrimaryEvidenceType", "CandidateStage" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "EmailFilingResolutionCandidates");

            migrationBuilder.DropIndex(
                name: "IX_EmailFilingResolutionRuns_AuthorityDecisionClass",
                table: "EmailFilingResolutionRuns");

            migrationBuilder.DropColumn(
                name: "AuthorityDecisionClass",
                table: "EmailFilingResolutionRuns");

            migrationBuilder.DropColumn(
                name: "DecisiveSupportingEvidenceTypeMask",
                table: "EmailFilingResolutionRuns");

            migrationBuilder.DropColumn(
                name: "PreferredClaimUsed",
                table: "EmailFilingResolutionRuns");

            migrationBuilder.DropColumn(
                name: "SourceTemplateKind",
                table: "EmailFilingResolutionRuns");
        }
    }
}
