using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Odmon.Worker.Migrations
{
    /// <inheritdoc />
    public partial class AddEmailFilingProcessingCheckpoint : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "NextLink",
                table: "EmailFilingMailboxStates",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ProcessedContentFingerprint",
                table: "EmailFilingDiagnostics",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "NextLink",
                table: "EmailFilingMailboxStates");

            migrationBuilder.DropColumn(
                name: "ProcessedContentFingerprint",
                table: "EmailFilingDiagnostics");
        }
    }
}
