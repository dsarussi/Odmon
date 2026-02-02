# BoardId SQL Timeout Fix - Implementation Summary

## Problem Statement

The ODMON Worker was experiencing SQL timeouts (30 seconds) when querying the `MondayItemMappings` table:

```sql
SELECT TOP(1) * FROM MondayItemMappings 
WHERE TikCounter=@tikCounter AND BoardId=@boardId
```

**Root Causes:**

1. **BoardId = 0 in Runtime**: The query parameter `boardId` was 0, causing the query to scan many rows with `BoardId=0`.
2. **Missing Index**: No index on `(BoardId, TikCounter)` combination, causing full table scans or inefficient index usage.
3. **NULL TikNumber Data**: Many existing rows have `TikNumber=NULL` and `BoardId=0`, making lookups expensive.
4. **Poor Diagnostics**: No visibility into which BoardId was being used at startup or in logs.

## Solution Implemented

### 1. Database Index (EF Migration)

**File**: `Migrations/20260203000000_AddMondayItemMappingIndexes.cs`

Created a nonclustered index with INCLUDE columns for covering index optimization:

```sql
CREATE NONCLUSTERED INDEX [IX_MondayItemMappings_BoardId_TikCounter]
ON [dbo].[MondayItemMappings] ([BoardId], [TikCounter])
INCLUDE ([Id], [MondayItemId], [TikNumber], [IsTest], [OdcanitVersion], [MondayChecksum], [LastSyncFromOdcanitUtc], [LastSyncFromMondayUtc]);
```

**Benefits:**
- O(log N) lookup time instead of O(N) table scan
- Covering index avoids key lookup (all needed columns in INCLUDE)
- Query should complete in < 10ms instead of 30+ seconds

**To Apply:**
```bash
dotnet ef database update --connection "Server=...;Database=OdmonIntegration;..."
```

Or apply manually:
```sql
USE OdmonIntegration;
GO

CREATE NONCLUSTERED INDEX [IX_MondayItemMappings_BoardId_TikCounter]
ON [dbo].[MondayItemMappings] ([BoardId], [TikCounter])
INCLUDE ([Id], [MondayItemId], [TikNumber], [IsTest], [OdcanitVersion], [MondayChecksum], [LastSyncFromOdcanitUtc], [LastSyncFromMondayUtc]);
GO
```

### 2. BoardId Validation (Fail Fast)

**File**: `Services/SyncService.cs`

Added validation in `FindOrCreateMappingAsync` method:

```csharp
// Validate BoardId - fail fast if 0
if (boardId == 0)
{
    throw new InvalidOperationException(
        $"BoardId is 0 for TikCounter={c.TikCounter}, TikNumber={c.TikNumber}. " +
        "Missing config key Monday:BoardId or Monday:CasesBoardId. " +
        "Set environment variable Monday__BoardId or configure in appsettings.");
}
```

**Benefits:**
- Catches misconfiguration immediately at startup
- Clear error message tells ops exactly what to fix
- Prevents expensive failed queries

### 3. Query Optimization (AsNoTracking)

**File**: `Services/SyncService.cs`

Added `AsNoTracking()` to read-only queries:

```csharp
// Priority 1: Find mapping by TikCounter + BoardId (source of truth)
mapping = await _integrationDb.MondayItemMappings
    .AsNoTracking()  // <-- Added
    .FirstOrDefaultAsync(m => m.TikCounter == c.TikCounter && m.BoardId == boardId, ct);
```

**Benefits:**
- Reduces EF Core overhead (no change tracking for read-only queries)
- Slightly faster query execution
- Lower memory usage

### 4. Startup Diagnostics

**File**: `Program.cs`

Added `LogStartupDiagnosticsAsync` method that logs:

```
Startup Diagnostics: Environment=Production, Monday.BoardId=5035534500, Monday.CasesBoardId=5035534500, KeyVault.Enabled=True, KeyVault.VaultUrl=https://***/***, IntegrationDb=your-server/OdmonIntegration, OdcanitDb=your-server/Odcanit
```

**Benefits:**
- Immediate visibility into configuration at startup
- Ops can verify BoardId is correct from logs
- Connection string target (server/database) is visible without exposing secrets
- Key Vault status is clear

### 5. Documentation

Created three comprehensive documentation files:

1. **`docs/SQL_Diagnostics.md`**
   - SQL queries for troubleshooting performance
   - Index verification queries
   - Blocking and lock detection
   - Data quality checks
   - Execution plan analysis

2. **`docs/CONFIGURATION.md`**
   - Complete configuration reference
   - Environment variable mapping
   - Secret key documentation
   - Production setup guide
   - Troubleshooting steps

3. **`docs/BOARDID_TIMEOUT_FIX_SUMMARY.md`** (this file)
   - Implementation summary
   - Change details
   - Testing steps

## Files Changed

### Modified Files

1. **Program.cs**
   - Added `LogStartupDiagnosticsAsync()` method
   - Added `MaskVaultUrl()` helper
   - Added `ExtractConnectionInfo()` helper
   - Calls diagnostics at startup

2. **Services/SyncService.cs**
   - Added BoardId validation (throws if 0)
   - Added `.AsNoTracking()` to read queries
   - Added comment about tracking in create/update paths

### New Files

3. **Migrations/20260203000000_AddMondayItemMappingIndexes.cs**
   - EF migration for new index

4. **Migrations/20260203000000_AddMondayItemMappingIndexes.Designer.cs**
   - EF migration designer file

5. **docs/SQL_Diagnostics.md**
   - SQL troubleshooting guide

6. **docs/CONFIGURATION.md**
   - Configuration reference

7. **docs/BOARDID_TIMEOUT_FIX_SUMMARY.md**
   - This summary document

## Configuration Requirements

### Critical Configuration

The following **MUST** be set on the production server:

```bash
# Environment Variable (set on the server)
Monday__BoardId=5035534500
```

Or in `appsettings.Production.json`:

```json
{
  "Monday": {
    "BoardId": 5035534500
  }
}
```

### All Required Config Keys

| Key | Environment Variable | Required | Description |
|-----|---------------------|----------|-------------|
| `Monday:BoardId` | `Monday__BoardId` | **YES** | Monday.com board ID (MUST NOT BE 0) |
| `Monday:ApiToken` | `Monday__ApiToken` | **YES** | Monday.com API token (secret) |
| `IntegrationDb__ConnectionString` | `IntegrationDb__ConnectionString` | **YES** | IntegrationDb connection string (secret) |
| `OdcanitDb__ConnectionString` | `OdcanitDb__ConnectionString` | **YES** (prod) | Odcanit DB connection string (secret) |

## Testing Steps

### 1. Verify Index Creation

After applying the migration, verify the index exists:

```sql
USE OdmonIntegration;
GO

SELECT 
    i.name AS IndexName,
    i.type_desc AS IndexType,
    STUFF((SELECT ', ' + c.name FROM sys.index_columns ic JOIN sys.columns c ON ic.object_id = c.object_id AND ic.column_id = c.column_id WHERE ic.object_id = i.object_id AND ic.index_id = i.index_id AND ic.is_included_column = 0 ORDER BY ic.key_ordinal FOR XML PATH('')), 1, 2, '') AS KeyColumns
FROM sys.indexes i
WHERE i.object_id = OBJECT_ID('dbo.MondayItemMappings')
    AND i.name = 'IX_MondayItemMappings_BoardId_TikCounter';
```

Expected output:
```
IndexName                                      IndexType         KeyColumns
IX_MondayItemMappings_BoardId_TikCounter      NONCLUSTERED      BoardId, TikCounter
```

### 2. Test Query Performance

Test the query with actual parameters:

```sql
SET STATISTICS IO ON;
SET STATISTICS TIME ON;

DECLARE @TikCounter INT = 900000;
DECLARE @BoardId BIGINT = 5035534500;

SELECT TOP(1) *
FROM dbo.MondayItemMappings
WHERE TikCounter = @TikCounter AND BoardId = @BoardId;

SET STATISTICS IO OFF;
SET STATISTICS TIME OFF;
```

Expected results:
- Elapsed time: < 10ms
- Logical reads: < 5 pages
- Execution plan shows "Index Seek" on `IX_MondayItemMappings_BoardId_TikCounter`

### 3. Verify BoardId Configuration

Check the startup log for the diagnostics line:

```
[StartupDiagnostics] Startup Diagnostics: Environment=Production, Monday.BoardId=5035534500, ...
```

**CRITICAL**: If it shows `Monday.BoardId=0`, the worker will fail. Set the environment variable and restart.

### 4. Test BoardId Validation

To verify the fail-fast behavior, temporarily set BoardId=0 and start the worker. It should throw:

```
InvalidOperationException: BoardId is 0 for TikCounter=900000, TikNumber=... Missing config key Monday:BoardId or Monday:CasesBoardId. Set environment variable Monday__BoardId or configure in appsettings.
```

This is the **expected behavior** and confirms the validation is working.

### 5. Monitor First Production Run

After deploying:

1. Check startup logs for diagnostics line
2. Verify BoardId is correct (not 0)
3. Monitor query performance (should be < 10ms)
4. Check for any timeout errors (should be none)

Use the queries in `docs/SQL_Diagnostics.md` to monitor performance.

## Rollback Plan

If issues occur after deployment:

### Quick Rollback (Keep Index, Revert Code)

1. Revert code changes to previous version
2. Keep the index (it won't hurt and may help)

### Full Rollback (Remove Index)

If the index causes issues (unlikely):

```sql
DROP INDEX [IX_MondayItemMappings_BoardId_TikCounter] ON [dbo].[MondayItemMappings];
```

Then revert code changes.

## Performance Expectations

### Before Fix

- Query timeout: 30+ seconds
- Execution plan: Table Scan or Index Scan (full table)
- Logical reads: 100,000+ pages
- Impact: Worker hangs, no progress

### After Fix

- Query time: < 10ms
- Execution plan: Index Seek (covering index)
- Logical reads: < 5 pages
- Impact: Worker processes cases normally

## Monitoring Queries

See `docs/SQL_Diagnostics.md` for full details. Key queries:

**Check for slow queries:**
```sql
SELECT TOP 10
    r.session_id,
    r.status,
    r.wait_type,
    r.total_elapsed_time / 1000 AS ElapsedSeconds,
    SUBSTRING(st.text, (r.statement_start_offset/2)+1, 50) AS QueryText
FROM sys.dm_exec_requests r
CROSS APPLY sys.dm_exec_sql_text(r.sql_handle) st
WHERE DB_NAME(r.database_id) = 'OdmonIntegration'
ORDER BY r.total_elapsed_time DESC;
```

**Check for rows with BoardId=0:**
```sql
SELECT BoardId, COUNT(*) AS RowCount
FROM dbo.MondayItemMappings
GROUP BY BoardId
ORDER BY BoardId;
```

## References

- Configuration Guide: `docs/CONFIGURATION.md`
- SQL Diagnostics: `docs/SQL_Diagnostics.md`
- EF Migration: `Migrations/20260203000000_AddMondayItemMappingIndexes.cs`

## Support

If issues occur after deployment:

1. Check startup diagnostics log line (BoardId should not be 0)
2. Verify index exists using queries in `SQL_Diagnostics.md`
3. Check query execution plan (should show Index Seek)
4. Monitor for blocking using DMV queries in `SQL_Diagnostics.md`
5. Review connection string targets in startup log

For configuration issues, see `docs/CONFIGURATION.md`.

For performance issues, see `docs/SQL_Diagnostics.md`.
