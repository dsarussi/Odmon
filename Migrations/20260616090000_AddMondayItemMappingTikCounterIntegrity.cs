using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Odmon.Worker.Migrations
{
    /// <inheritdoc />
    public partial class AddMondayItemMappingTikCounterIntegrity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
IF NOT EXISTS (
    SELECT 1
    FROM sys.check_constraints
    WHERE name = N'CK_MondayItemMappings_TikCounter_Positive'
      AND parent_object_id = OBJECT_ID(N'[dbo].[MondayItemMappings]')
)
BEGIN
    ALTER TABLE [dbo].[MondayItemMappings]
    ADD CONSTRAINT [CK_MondayItemMappings_TikCounter_Positive]
    CHECK ([TikCounter] > 0);
END
");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
IF EXISTS (
    SELECT 1
    FROM sys.check_constraints
    WHERE name = N'CK_MondayItemMappings_TikCounter_Positive'
      AND parent_object_id = OBJECT_ID(N'[dbo].[MondayItemMappings]')
)
BEGIN
    ALTER TABLE [dbo].[MondayItemMappings]
    DROP CONSTRAINT [CK_MondayItemMappings_TikCounter_Positive];
END
");
        }
    }
}
