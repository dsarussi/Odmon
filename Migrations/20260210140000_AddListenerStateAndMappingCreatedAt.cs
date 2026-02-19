using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Odmon.Worker.Migrations
{
    /// <inheritdoc />
    public partial class AddListenerStateAndMappingCreatedAt : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // 1) Add CreatedAtUtc to MondayItemMappings (backfill old rows with 2000-01-01)
            migrationBuilder.Sql(@"
IF NOT EXISTS (
    SELECT 1 FROM sys.columns
    WHERE object_id = OBJECT_ID(N'[dbo].[MondayItemMappings]')
      AND name = N'CreatedAtUtc'
)
BEGIN
    ALTER TABLE [dbo].[MondayItemMappings]
        ADD [CreatedAtUtc] datetime2 NOT NULL
            CONSTRAINT [DF_MondayItemMappings_CreatedAtUtc] DEFAULT (SYSUTCDATETIME());

    -- Backfill existing rows so they are excluded by T0 filter
    UPDATE [dbo].[MondayItemMappings]
        SET [CreatedAtUtc] = '2000-01-01T00:00:00'
        WHERE [CreatedAtUtc] > '2025-01-01';
END
");

            // 2) Create ListenerState singleton table
            migrationBuilder.Sql(@"
IF OBJECT_ID(N'[dbo].[ListenerState]', 'U') IS NULL
BEGIN
    CREATE TABLE [dbo].[ListenerState] (
        [Id]                          int        NOT NULL,
        [StartedAtUtc]                datetime2  NOT NULL,
        [LastChangeFeedWatermarkUtc]  datetime2  NULL,
        [UpdatedAtUtc]                datetime2  NOT NULL,
        CONSTRAINT [PK_ListenerState] PRIMARY KEY ([Id])
    );
END
");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
IF EXISTS (
    SELECT 1 FROM sys.columns
    WHERE object_id = OBJECT_ID(N'[dbo].[MondayItemMappings]')
      AND name = N'CreatedAtUtc'
)
BEGIN
    ALTER TABLE [dbo].[MondayItemMappings]
        DROP CONSTRAINT IF EXISTS [DF_MondayItemMappings_CreatedAtUtc];
    ALTER TABLE [dbo].[MondayItemMappings]
        DROP COLUMN [CreatedAtUtc];
END
");

            migrationBuilder.Sql(@"
DROP TABLE IF EXISTS [dbo].[ListenerState];
");
        }
    }
}
