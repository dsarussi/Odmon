using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Odmon.Worker.Migrations
{
    /// <inheritdoc />
    public partial class AddEmailAutomationCaseRoutingAudit : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ActualForwardTo",
                table: "EmailAutomationLogs",
                type: "nvarchar(320)",
                maxLength: 320,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ResolvedClientNumber",
                table: "EmailAutomationLogs",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ResolvedTargetEmail",
                table: "EmailAutomationLogs",
                type: "nvarchar(320)",
                maxLength: 320,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ResolvedTikCounter",
                table: "EmailAutomationLogs",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ResolvedTikNumber",
                table: "EmailAutomationLogs",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ActualForwardTo",
                table: "EmailAutomationLogs");

            migrationBuilder.DropColumn(
                name: "ResolvedClientNumber",
                table: "EmailAutomationLogs");

            migrationBuilder.DropColumn(
                name: "ResolvedTargetEmail",
                table: "EmailAutomationLogs");

            migrationBuilder.DropColumn(
                name: "ResolvedTikCounter",
                table: "EmailAutomationLogs");

            migrationBuilder.DropColumn(
                name: "ResolvedTikNumber",
                table: "EmailAutomationLogs");
        }
    }
}
