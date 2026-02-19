using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Odmon.Worker.Migrations
{
    /// <inheritdoc />
    public partial class FixMissingHearingNearestSnapshots : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
IF OBJECT_ID(N'[dbo].[HearingNearestSnapshots]', 'U') IS NULL
BEGIN
    CREATE TABLE [dbo].[HearingNearestSnapshots] (
        [Id]                  int           NOT NULL IDENTITY(1,1),
        [TikCounter]          int           NOT NULL,
        [BoardId]             bigint        NOT NULL,
        [MondayItemId]        bigint        NOT NULL,
        [NearestStartDateUtc] datetime2     NULL,
        [NearestMeetStatus]   int           NULL,
        [JudgeName]           nvarchar(max) NULL,
        [City]                nvarchar(max) NULL,
        [LastSyncedAtUtc]     datetime2     NOT NULL,
        CONSTRAINT [PK_HearingNearestSnapshots] PRIMARY KEY ([Id])
    );

    CREATE INDEX [IX_HearingNearestSnapshots_MondayItemId]
        ON [dbo].[HearingNearestSnapshots] ([MondayItemId]);

    CREATE UNIQUE INDEX [IX_HearingNearestSnapshots_TikCounter_BoardId]
        ON [dbo].[HearingNearestSnapshots] ([TikCounter], [BoardId]);
END
");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Intentionally empty: do not drop a table that may have been
            // created by the original migration on other environments.
        }
    }
}
