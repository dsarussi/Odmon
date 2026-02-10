-- ============================================================================
-- ODMON IntegrationDb: Cleanup historical/test data
-- ============================================================================
-- Purpose: Remove old test/incorrect rows from IntegrationDb so that
--          hearing services and sync services no longer process stale cases.
--
-- Usage:
--   1. Run the SELECT preview sections first to verify what will be deleted.
--   2. Uncomment the DELETE sections when ready.
--   3. Adjust the TikCounter/TikNumber lists below to match your environment.
--
-- Safe: wrapped in explicit transaction with ROLLBACK for preview.
-- ============================================================================

-- ── Parameters: edit these to match your environment ──
DECLARE @BoardId         BIGINT = 5035534500;
DECLARE @TestTikCounters TABLE (TikCounter INT);
DECLARE @TestTikNumbers  TABLE (TikNumber  NVARCHAR(50));

-- Known test TikCounters
INSERT INTO @TestTikCounters VALUES (37746), (37748), (37747), (39231), (39232);

-- Known test TikNumbers
INSERT INTO @TestTikNumbers VALUES ('8/469'), ('8/470'), ('7/1235657');

-- ============================================================================
-- PREVIEW: Show what will be deleted
-- ============================================================================

PRINT '=== PREVIEW: MondayItemMappings to delete ===';
SELECT m.Id, m.TikCounter, m.TikNumber, m.MondayItemId, m.BoardId, m.IsTest, m.CreatedAtUtc
FROM dbo.MondayItemMappings m
WHERE m.IsTest = 1
   OR m.TikCounter IN (SELECT TikCounter FROM @TestTikCounters)
   OR m.TikNumber IN (SELECT TikNumber FROM @TestTikNumbers);

PRINT '=== PREVIEW: HearingNearestSnapshots to delete ===';
SELECT s.Id, s.TikCounter, s.BoardId, s.MondayItemId, s.LastSyncedAtUtc
FROM dbo.HearingNearestSnapshots s
WHERE s.BoardId = @BoardId
  AND (
    s.TikCounter IN (SELECT TikCounter FROM @TestTikCounters)
    OR s.TikCounter IN (
        SELECT m.TikCounter FROM dbo.MondayItemMappings m
        WHERE m.IsTest = 1
    )
    OR NOT EXISTS (
        SELECT 1 FROM dbo.MondayItemMappings m2
        WHERE m2.TikCounter = s.TikCounter AND m2.BoardId = s.BoardId
    )
  );

PRINT '=== PREVIEW: MondayHearingApprovalStates to delete ===';
SELECT a.Id, a.TikCounter, a.BoardId, a.MondayItemId, a.UpdatedAtUtc
FROM dbo.MondayHearingApprovalStates a
WHERE a.TikCounter IN (SELECT TikCounter FROM @TestTikCounters);

-- ============================================================================
-- DELETE: Uncomment the block below when ready to execute
-- ============================================================================

/*
BEGIN TRANSACTION;

-- 1) Delete snapshots for test/orphaned cases
DELETE s
FROM dbo.HearingNearestSnapshots s
WHERE s.BoardId = @BoardId
  AND (
    s.TikCounter IN (SELECT TikCounter FROM @TestTikCounters)
    OR s.TikCounter IN (
        SELECT m.TikCounter FROM dbo.MondayItemMappings m
        WHERE m.IsTest = 1
    )
    OR NOT EXISTS (
        SELECT 1 FROM dbo.MondayItemMappings m2
        WHERE m2.TikCounter = s.TikCounter AND m2.BoardId = s.BoardId
    )
  );

PRINT 'Deleted HearingNearestSnapshots: ' + CAST(@@ROWCOUNT AS NVARCHAR);

-- 2) Delete approval states for test cases
DELETE a
FROM dbo.MondayHearingApprovalStates a
WHERE a.TikCounter IN (SELECT TikCounter FROM @TestTikCounters);

PRINT 'Deleted MondayHearingApprovalStates: ' + CAST(@@ROWCOUNT AS NVARCHAR);

-- 3) Delete test/incorrect mappings
DELETE m
FROM dbo.MondayItemMappings m
WHERE m.IsTest = 1
   OR m.TikCounter IN (SELECT TikCounter FROM @TestTikCounters)
   OR m.TikNumber IN (SELECT TikNumber FROM @TestTikNumbers);

PRINT 'Deleted MondayItemMappings: ' + CAST(@@ROWCOUNT AS NVARCHAR);

-- 4) Delete sync failures for test cases
DELETE f
FROM dbo.SyncFailures f
WHERE f.TikCounter IN (SELECT TikCounter FROM @TestTikCounters);

PRINT 'Deleted SyncFailures: ' + CAST(@@ROWCOUNT AS NVARCHAR);

COMMIT TRANSACTION;
PRINT 'Cleanup completed successfully.';
*/

-- To rollback instead of commit (for dry-run), change COMMIT to ROLLBACK above.
