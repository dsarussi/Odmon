/*
  Manual migration — DO NOT run automatically from CI/deploy.
  Purpose: support common Integration DB access patterns on dbo.MondayItemMappings.

  Verify before applying:
  - Table name matches your database (default EF: MondayItemMappings).
  - Existing indexes: you may already have IX_MondayItemMappings_TikCounter (unique) from EF;
    the composite (BoardId, TikCounter) index is additive for board-scoped lookups.

  Recommended: run in a maintenance window; ONLINE = ON requires Enterprise/Developer (SQL Server).
*/

IF NOT EXISTS (
    SELECT 1 FROM sys.indexes
    WHERE name = N'IX_MondayItemMappings_BoardId_TikCounter'
      AND object_id = OBJECT_ID(N'dbo.MondayItemMappings'))
BEGIN
    CREATE NONCLUSTERED INDEX IX_MondayItemMappings_BoardId_TikCounter
        ON dbo.MondayItemMappings (BoardId, TikCounter);
END
GO

IF NOT EXISTS (
    SELECT 1 FROM sys.indexes
    WHERE name = N'IX_MondayItemMappings_CreatedAtUtc'
      AND object_id = OBJECT_ID(N'dbo.MondayItemMappings'))
BEGIN
    CREATE NONCLUSTERED INDEX IX_MondayItemMappings_CreatedAtUtc
        ON dbo.MondayItemMappings (CreatedAtUtc);
END
GO

IF NOT EXISTS (
    SELECT 1 FROM sys.indexes
    WHERE name = N'IX_MondayItemMappings_LastSyncFromOdcanitUtc'
      AND object_id = OBJECT_ID(N'dbo.MondayItemMappings'))
BEGIN
    CREATE NONCLUSTERED INDEX IX_MondayItemMappings_LastSyncFromOdcanitUtc
        ON dbo.MondayItemMappings (LastSyncFromOdcanitUtc);
END
GO
