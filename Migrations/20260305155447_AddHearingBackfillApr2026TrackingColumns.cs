using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Odmon.Worker.Migrations
{
    /// <inheritdoc />
    public partial class AddHearingBackfillApr2026TrackingColumns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Add tracking columns only if they don't exist (table may already exist with source columns)
            migrationBuilder.Sql(@"
IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID(N'[dbo].[HearingBackfill_Apr2026]') AND name = N'ImportStatus')
BEGIN
    ALTER TABLE [dbo].[HearingBackfill_Apr2026] ADD [ImportStatus] nvarchar(20) NOT NULL CONSTRAINT [DF_HearingBackfill_Apr2026_ImportStatus] DEFAULT N'Pending';
END

IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID(N'[dbo].[HearingBackfill_Apr2026]') AND name = N'ImportedAtUtc')
BEGIN
    ALTER TABLE [dbo].[HearingBackfill_Apr2026] ADD [ImportedAtUtc] datetime2(3) NULL;
END

IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID(N'[dbo].[HearingBackfill_Apr2026]') AND name = N'MondayItemId')
BEGIN
    ALTER TABLE [dbo].[HearingBackfill_Apr2026] ADD [MondayItemId] bigint NULL;
END

IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID(N'[dbo].[HearingBackfill_Apr2026]') AND name = N'ImportError')
BEGIN
    ALTER TABLE [dbo].[HearingBackfill_Apr2026] ADD [ImportError] nvarchar(4000) NULL;
END
");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
IF EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID(N'[dbo].[HearingBackfill_Apr2026]') AND name = N'ImportStatus')
BEGIN
    ALTER TABLE [dbo].[HearingBackfill_Apr2026] DROP CONSTRAINT IF EXISTS [DF_HearingBackfill_Apr2026_ImportStatus];
    ALTER TABLE [dbo].[HearingBackfill_Apr2026] DROP COLUMN [ImportStatus];
END

IF EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID(N'[dbo].[HearingBackfill_Apr2026]') AND name = N'ImportedAtUtc')
BEGIN
    ALTER TABLE [dbo].[HearingBackfill_Apr2026] DROP COLUMN [ImportedAtUtc];
END

IF EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID(N'[dbo].[HearingBackfill_Apr2026]') AND name = N'MondayItemId')
BEGIN
    ALTER TABLE [dbo].[HearingBackfill_Apr2026] DROP COLUMN [MondayItemId];
END

IF EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID(N'[dbo].[HearingBackfill_Apr2026]') AND name = N'ImportError')
BEGIN
    ALTER TABLE [dbo].[HearingBackfill_Apr2026] DROP COLUMN [ImportError];
END
");
        }
    }
}
