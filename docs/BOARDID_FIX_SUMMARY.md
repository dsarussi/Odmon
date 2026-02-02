# BoardId Fix Summary - MondayItemMappings SQL Timeout Resolution

## Problem
SQL timeout (30s) on query `SELECT TOP(1) ... FROM MondayItemMappings WHERE TikCounter=@tikCounter AND BoardId=@boardId` when BoardId=0, causing table scans.

## Root Cause
1. BoardId was 0 at runtime due to missing configuration
2. Missing index on (BoardId, TikCounter) causing table scans
3. No fail-fast validation when BoardId is invalid

## Changes Applied

### 1. Fail-Fast Validation for BoardId

**File: `Services/SyncService.cs`**

- Added validation in `SyncOdcanitToMondayAsync`:
  ```csharp
  if (casesBoardId == 0)
  {
      throw new InvalidOperationException(
          "Monday:CasesBoardId is 0 or missing. " +
          "Set configuration key 'Monday:CasesBoardId' (or environment variable 'Monday__CasesBoardId') to a valid Monday.com board ID.");
  }
  ```

- Added validation in `FindOrCreateMappingAsync`:
  ```csharp
  if (boardId == 0)
  {
      throw new InvalidOperationException(
          "BoardId is 0 - missing config key Monday:CasesBoardId (or Monday__CasesBoardId environment variable). " +
          $"Cannot sync TikCounter={c.TikCounter}, TikNumber={c.TikNumber ?? "<null>"}.");
  }
  ```

### 2. Performance Optimization - AsNoTracking()

**File: `Services/SyncService.cs`**

- Updated `FindOrCreateMappingAsync` to use `AsNoTracking()` for read-only queries:
  ```csharp
  mapping = await _integrationDb.MondayItemMappings
      .AsNoTracking()
      .FirstOrDefaultAsync(m => m.TikCounter == c.TikCounter && m.BoardId == boardId, ct);
  ```

### 3. Database Index Migration

**File: `Data/IntegrationDbContext.cs`**

- Added index configuration:
  ```csharp
  modelBuilder.Entity<MondayItemMapping>()
      .HasIndex(m => new { m.BoardId, m.TikCounter })
      .IncludeProperties(m => new { m.Id, m.MondayItemId, m.TikNumber, m.IsTest, m.OdcanitVersion, m.MondayChecksum, m.LastSyncFromOdcanitUtc, m.LastSyncFromMondayUtc });
  ```

**Migration: `Migrations/20260202141925_AddMondayItemMappingsIndexes.cs`**

- Creates nonclustered index: `IX_MondayItemMappings_BoardId_TikCounter` on (BoardId, TikCounter) with INCLUDE columns

**SQL Index Definition:**
```sql
CREATE NONCLUSTERED INDEX [IX_MondayItemMappings_BoardId_TikCounter]
ON [dbo].[MondayItemMappings] ([BoardId], [TikCounter])
INCLUDE ([Id], [MondayItemId], [TikNumber], [IsTest], [OdcanitVersion], [MondayChecksum], [LastSyncFromOdcanitUtc], [LastSyncFromMondayUtc]);
```

### 4. Startup Diagnostic Logging

**File: `Program.cs`**

- Added startup diagnostics that log:
  - Environment name
  - Monday board ID (actual value used)
  - IntegrationDb connection string target (server/db name only, no secrets)
  - KeyVault Enabled/VaultUrl (masked)

**Log Output Example:**
```
ODMON Startup Diagnostics: Environment=Production, MondayBoardId=5035534500, IntegrationDbServer=myserver.database.windows.net, IntegrationDbName=OdmonIntegration, KeyVaultEnabled=True, KeyVaultUrl=mykv***.vault.azure.net
```

## Configuration Keys Required on Server

### Required Configuration Keys

| Config Key | Environment Variable | Description | Example |
|------------|---------------------|-------------|---------|
| `Monday:CasesBoardId` | `Monday__CasesBoardId` | Monday.com board ID for production cases | `5035534500` |
| `Monday:BoardId` | `Monday__BoardId` | Legacy/fallback board ID | `5035534500` |
| `Safety:TestBoardId` | `Safety__TestBoardId` | Monday.com board ID for test mode (optional) | `5035534500` |
| `Monday:ToDoGroupId` | `Monday__ToDoGroupId` | Monday.com group ID for items | `"new_group"` |
| `Monday:TestGroupId` | `Monday__TestGroupId` | Monday.com group ID for test items (optional) | `"test_group"` |
| `IntegrationDb__ConnectionString` | `IntegrationDb__ConnectionString` | SQL Server connection string for IntegrationDb | `"Server=...;Database=...;..."` |
| `OdcanitDb__ConnectionString` | `OdcanitDb__ConnectionString` | SQL Server connection string for OdcanitDb (Production only) | `"Server=...;Database=...;..."` |
| `Monday__ApiToken` | `Monday__ApiToken` | Monday.com API token | `"eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9..."` |

### Environment Variable Naming Convention

- Configuration keys use `:` separator (e.g., `Monday:CasesBoardId`)
- Environment variables use `__` separator (e.g., `Monday__CasesBoardId`)
- Connection strings use `__` separator (e.g., `IntegrationDb__ConnectionString`)

### Priority Order for Configuration Resolution

1. Azure Key Vault (if `KeyVault:Enabled=true`)
2. Environment Variables
3. appsettings.json / appsettings.{Environment}.json
4. User Secrets (Development only)

## Migration Steps

1. **Apply EF Migration:**
   ```bash
   dotnet ef database update --context IntegrationDbContext
   ```

2. **Verify Index Creation:**
   ```sql
   SELECT name, type_desc, is_unique
   FROM sys.indexes
   WHERE object_id = OBJECT_ID('dbo.MondayItemMappings')
       AND name = 'IX_MondayItemMappings_BoardId_TikCounter';
   ```

3. **Verify Configuration:**
   - Ensure `Monday:CasesBoardId` is set to a non-zero value
   - Check startup logs for diagnostic output

## Expected Behavior After Fix

- **Before:** Query timeout (30s) when BoardId=0, table scan on MondayItemMappings
- **After:** 
  - Fail-fast error if BoardId=0 (clear error message with config key name)
  - O(log N) lookup using index `IX_MondayItemMappings_BoardId_TikCounter`
  - Startup diagnostics show actual BoardId value
  - AsNoTracking() reduces EF overhead for read-only queries

## SQL Diagnostic Queries

See `docs/SQL_DIAGNOSTICS.md` for:
- Blocking query detection
- Index usage statistics
- Execution plan analysis
- Data distribution checks
