using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Odmon.Worker.Migrations
{
    /// <inheritdoc />
    public partial class AddMondayItemMappingIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Add optimized nonclustered index on (BoardId, TikCounter) with INCLUDE columns for efficient lookups
            migrationBuilder.Sql(@"
                CREATE NONCLUSTERED INDEX [IX_MondayItemMappings_BoardId_TikCounter]
                ON [dbo].[MondayItemMappings] ([BoardId], [TikCounter])
                INCLUDE ([Id], [MondayItemId], [TikNumber], [IsTest], [OdcanitVersion], [MondayChecksum], [LastSyncFromOdcanitUtc], [LastSyncFromMondayUtc]);
            ");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_MondayItemMappings_BoardId_TikCounter",
                table: "MondayItemMappings");
        }
    }
}
