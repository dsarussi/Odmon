using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Odmon.Worker.Migrations
{
    /// <inheritdoc />
    public partial class AddNispahDedupAndAuditTables : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Idempotent: create tables only if missing (e.g. migration history out of sync with DB).
            // Matches schema from AddHearingNearestSnapshot; safe to run when tables already exist.

            migrationBuilder.Sql(@"
IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE object_id = OBJECT_ID(N'dbo.NispahAuditLogs'))
BEGIN
    CREATE TABLE [dbo].[NispahAuditLogs] (
        [Id] bigint NOT NULL IDENTITY(1, 1),
        [CreatedAtUtc] datetime2 NOT NULL,
        [CorrelationId] nvarchar(450) NOT NULL,
        [TikVisualID] nvarchar(450) NOT NULL,
        [NispahTypeName] nvarchar(max) NOT NULL,
        [InfoLength] int NOT NULL,
        [InfoHash] nvarchar(max) NOT NULL,
        [Status] nvarchar(max) NOT NULL,
        [Error] nvarchar(max) NULL,
        CONSTRAINT [PK_NispahAuditLogs] PRIMARY KEY ([Id])
    );
    CREATE INDEX [IX_NispahAuditLogs_CorrelationId] ON [dbo].[NispahAuditLogs] ([CorrelationId]);
    CREATE INDEX [IX_NispahAuditLogs_TikVisualID_CreatedAtUtc] ON [dbo].[NispahAuditLogs] ([TikVisualID], [CreatedAtUtc]);
END;

IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE object_id = OBJECT_ID(N'dbo.NispahDeduplications'))
BEGIN
    CREATE TABLE [dbo].[NispahDeduplications] (
        [Id] bigint NOT NULL IDENTITY(1, 1),
        [CreatedAtUtc] datetime2 NOT NULL,
        [TikVisualID] nvarchar(450) NOT NULL,
        [NispahTypeName] nvarchar(450) NOT NULL,
        [InfoHash] nvarchar(450) NOT NULL,
        CONSTRAINT [PK_NispahDeduplications] PRIMARY KEY ([Id])
    );
    CREATE INDEX [IX_NispahDeduplications_CreatedAtUtc] ON [dbo].[NispahDeduplications] ([CreatedAtUtc]);
    CREATE UNIQUE INDEX [IX_NispahDeduplications_TikVisualID_NispahTypeName_InfoHash] ON [dbo].[NispahDeduplications] ([TikVisualID], [NispahTypeName], [InfoHash]);
END;
");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Optional: drop tables if this migration is reverted. Leave as no-op if you prefer to never drop these tables.
            migrationBuilder.Sql(@"
IF EXISTS (SELECT 1 FROM sys.tables WHERE object_id = OBJECT_ID(N'dbo.NispahAuditLogs'))
    DROP TABLE [dbo].[NispahAuditLogs];
IF EXISTS (SELECT 1 FROM sys.tables WHERE object_id = OBJECT_ID(N'dbo.NispahDeduplications'))
    DROP TABLE [dbo].[NispahDeduplications];
");
        }
    }
}
