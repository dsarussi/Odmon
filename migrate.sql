IF OBJECT_ID(N'[__EFMigrationsHistory]') IS NULL
BEGIN
    CREATE TABLE [__EFMigrationsHistory] (
        [MigrationId] nvarchar(150) NOT NULL,
        [ProductVersion] nvarchar(32) NOT NULL,
        CONSTRAINT [PK___EFMigrationsHistory] PRIMARY KEY ([MigrationId])
    );
END;
GO

BEGIN TRANSACTION;
GO

CREATE TABLE [MondayItemMappings] (
    [Id] int NOT NULL IDENTITY,
    [TikCounter] int NOT NULL,
    [MondayItemId] bigint NOT NULL,
    [LastSyncFromOdcanitUtc] datetime2 NULL,
    [LastSyncFromMondayUtc] datetime2 NULL,
    [OdcanitVersion] nvarchar(max) NULL,
    [MondayChecksum] nvarchar(max) NULL,
    CONSTRAINT [PK_MondayItemMappings] PRIMARY KEY ([Id])
);
GO

CREATE TABLE [SyncLogs] (
    [Id] int NOT NULL IDENTITY,
    [CreatedAtUtc] datetime2 NOT NULL,
    [Source] nvarchar(max) NOT NULL,
    [Level] nvarchar(max) NOT NULL,
    [Message] nvarchar(max) NOT NULL,
    [Details] nvarchar(max) NULL,
    CONSTRAINT [PK_SyncLogs] PRIMARY KEY ([Id])
);
GO

CREATE INDEX [IX_MondayItemMappings_MondayItemId] ON [MondayItemMappings] ([MondayItemId]);
GO

CREATE INDEX [IX_MondayItemMappings_TikCounter] ON [MondayItemMappings] ([TikCounter]);
GO

INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
VALUES (N'20251109143557_InitialCreate', N'8.0.0');
GO

COMMIT;
GO

BEGIN TRANSACTION;
GO

CREATE TABLE [AllowedTik] (
    [TikCounter] int NOT NULL,
    CONSTRAINT [PK_AllowedTik] PRIMARY KEY ([TikCounter])
);
GO

INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
VALUES (N'20251112120000_AddAllowedTik', N'8.0.0');
GO

COMMIT;
GO

BEGIN TRANSACTION;
GO

DROP INDEX [IX_MondayItemMappings_TikCounter] ON [MondayItemMappings];
GO

ALTER TABLE [MondayItemMappings] ADD [BoardId] bigint NOT NULL DEFAULT CAST(0 AS bigint);
GO

ALTER TABLE [MondayItemMappings] ADD [IsTest] bit NOT NULL DEFAULT CAST(0 AS bit);
GO

ALTER TABLE [MondayItemMappings] ADD [TikNumber] nvarchar(450) NULL;
GO

CREATE TABLE [HearingNearestSnapshots] (
    [Id] int NOT NULL IDENTITY,
    [TikCounter] int NOT NULL,
    [BoardId] bigint NOT NULL,
    [MondayItemId] bigint NOT NULL,
    [NearestStartDateUtc] datetime2 NULL,
    [NearestMeetStatus] int NULL,
    [JudgeName] nvarchar(max) NULL,
    [City] nvarchar(max) NULL,
    [LastSyncedAtUtc] datetime2 NOT NULL,
    CONSTRAINT [PK_HearingNearestSnapshots] PRIMARY KEY ([Id])
);
GO

CREATE TABLE [MondayHearingApprovalStates] (
    [Id] int NOT NULL IDENTITY,
    [BoardId] bigint NOT NULL,
    [MondayItemId] bigint NOT NULL,
    [TikCounter] int NOT NULL,
    [LastKnownStatus] nvarchar(max) NULL,
    [FirstDecision] nvarchar(max) NULL,
    [LastWriteAtUtc] datetime2 NULL,
    [UpdatedAtUtc] datetime2 NOT NULL,
    CONSTRAINT [PK_MondayHearingApprovalStates] PRIMARY KEY ([Id])
);
GO

CREATE TABLE [NispahAuditLogs] (
    [Id] bigint NOT NULL IDENTITY,
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
GO

CREATE TABLE [NispahDeduplications] (
    [Id] bigint NOT NULL IDENTITY,
    [CreatedAtUtc] datetime2 NOT NULL,
    [TikVisualID] nvarchar(450) NOT NULL,
    [NispahTypeName] nvarchar(450) NOT NULL,
    [InfoHash] nvarchar(450) NOT NULL,
    CONSTRAINT [PK_NispahDeduplications] PRIMARY KEY ([Id])
);
GO

CREATE UNIQUE INDEX [IX_MondayItemMappings_TikCounter] ON [MondayItemMappings] ([TikCounter]);
GO

CREATE INDEX [IX_MondayItemMappings_TikNumber_BoardId] ON [MondayItemMappings] ([TikNumber], [BoardId]);
GO

CREATE INDEX [IX_HearingNearestSnapshots_MondayItemId] ON [HearingNearestSnapshots] ([MondayItemId]);
GO

CREATE UNIQUE INDEX [IX_HearingNearestSnapshots_TikCounter_BoardId] ON [HearingNearestSnapshots] ([TikCounter], [BoardId]);
GO

CREATE UNIQUE INDEX [IX_MondayHearingApprovalStates_BoardId_MondayItemId] ON [MondayHearingApprovalStates] ([BoardId], [MondayItemId]);
GO

CREATE INDEX [IX_NispahAuditLogs_CorrelationId] ON [NispahAuditLogs] ([CorrelationId]);
GO

CREATE INDEX [IX_NispahAuditLogs_TikVisualID_CreatedAtUtc] ON [NispahAuditLogs] ([TikVisualID], [CreatedAtUtc]);
GO

CREATE INDEX [IX_NispahDeduplications_CreatedAtUtc] ON [NispahDeduplications] ([CreatedAtUtc]);
GO

CREATE UNIQUE INDEX [IX_NispahDeduplications_TikVisualID_NispahTypeName_InfoHash] ON [NispahDeduplications] ([TikVisualID], [NispahTypeName], [InfoHash]);
GO

INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
VALUES (N'20260202084813_AddHearingNearestSnapshot', N'8.0.0');
GO

COMMIT;
GO

BEGIN TRANSACTION;
GO

INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
VALUES (N'20260202085642_AddHearingNearestSnapshot_Only', N'8.0.0');
GO

COMMIT;
GO

