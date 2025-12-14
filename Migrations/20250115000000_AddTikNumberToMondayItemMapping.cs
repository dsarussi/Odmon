using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Odmon.Worker.Migrations
{
    /// <inheritdoc />
    public partial class AddTikNumberToMondayItemMapping : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "TikNumber",
                table: "MondayItemMappings",
                type: "nvarchar(450)",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "BoardId",
                table: "MondayItemMappings",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.CreateIndex(
                name: "IX_MondayItemMappings_TikNumber_BoardId",
                table: "MondayItemMappings",
                columns: new[] { "TikNumber", "BoardId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_MondayItemMappings_TikNumber_BoardId",
                table: "MondayItemMappings");

            migrationBuilder.DropColumn(
                name: "TikNumber",
                table: "MondayItemMappings");

            migrationBuilder.DropColumn(
                name: "BoardId",
                table: "MondayItemMappings");
        }
    }
}

