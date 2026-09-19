using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Odmon.Worker.Migrations
{
    /// <inheritdoc />
    public partial class AddHearingSourceEventDeliveryState : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "DeliveredMeetStatus",
                table: "HearingNearestSnapshots",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "DeliveredStatusSourceEventId",
                table: "HearingNearestSnapshots",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ObservedSourceEventId",
                table: "HearingNearestSnapshots",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "PendingMeetStatus",
                table: "HearingNearestSnapshots",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "PendingInitialStatusWasDesired",
                table: "HearingNearestSnapshots",
                type: "bit",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "PendingStatusSourceEventId",
                table: "HearingNearestSnapshots",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "PendingStatusSinceUtc",
                table: "HearingNearestSnapshots",
                type: "datetime2",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_HearingNearestSnapshots_ObservedSourceEventId",
                table: "HearingNearestSnapshots",
                column: "ObservedSourceEventId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_HearingNearestSnapshots_ObservedSourceEventId",
                table: "HearingNearestSnapshots");

            migrationBuilder.DropColumn(
                name: "DeliveredMeetStatus",
                table: "HearingNearestSnapshots");

            migrationBuilder.DropColumn(
                name: "DeliveredStatusSourceEventId",
                table: "HearingNearestSnapshots");

            migrationBuilder.DropColumn(
                name: "ObservedSourceEventId",
                table: "HearingNearestSnapshots");

            migrationBuilder.DropColumn(
                name: "PendingMeetStatus",
                table: "HearingNearestSnapshots");

            migrationBuilder.DropColumn(
                name: "PendingInitialStatusWasDesired",
                table: "HearingNearestSnapshots");

            migrationBuilder.DropColumn(
                name: "PendingStatusSourceEventId",
                table: "HearingNearestSnapshots");

            migrationBuilder.DropColumn(
                name: "PendingStatusSinceUtc",
                table: "HearingNearestSnapshots");
        }
    }
}
