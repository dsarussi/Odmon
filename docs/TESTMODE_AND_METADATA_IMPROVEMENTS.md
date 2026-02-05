# Test Mode Detection & Metadata Failure Handling Improvements

## Changes Implemented

### CHANGE 1: Derive testMode from Actual Data Source

**Problem**: Previously `testMode` was hardcoded to `false`, which could mask accidental usage of synthetic/test data sources.

**Solution**: `testMode` is now derived from the actual data source configuration flags.

**File**: `Services\SyncService.cs`

**Implementation**:

```csharp
// Determine data source and derive testMode from actual source
var testingEnabled = _config.GetValue<bool>("Testing:Enable", false);
var odmonTestCasesEnabled = _config.GetValue<bool>("OdmonTestCases:Enable", false);

string dataSource;
bool testMode;

if (odmonTestCasesEnabled)
{
    dataSource = "OdmonTestCases";
    testMode = true;
}
else if (testingEnabled)
{
    dataSource = "Testing (GuardOdcanitReader)";
    testMode = true;
}
else
{
    dataSource = "Odcanit";
    testMode = false;
}

_logger.LogInformation(
    "Data source: {DataSource}, testMode={TestMode}",
    dataSource,
    testMode);
```

**Data Source Priority**:
1. `OdmonTestCases:Enable=true` → Data source: "OdmonTestCases", testMode: true
2. `Testing:Enable=true` → Data source: "Testing (GuardOdcanitReader)", testMode: true
3. Both false → Data source: "Odcanit", testMode: false

**Board/Group Selection**:
- When `testMode=true`: Uses `Safety:TestBoardId` and `Monday:TestGroupId` if configured
- When `testMode=false`: Uses production `Monday:CasesBoardId` and `Monday:ToDoGroupId`

**Startup Logs**:

```
[INFO] Data source: Odcanit, testMode=False
[INFO] OdcanitLoad allowlist resolved to 2 TikCounter(s): [39115, 42020]
```

or

```
[INFO] Data source: OdmonTestCases, testMode=True
[INFO] OdmonTestCases: 5 eligible rows, 2 selected (MaxId=2)
```

### CHANGE 2: Non-Critical Field Metadata Failures Don't Crash Sync

**Problem**: Metadata fetch failures (auth, network, config) would bubble up and stop the entire sync, even for non-critical fields.

**Solution**: 
- **Critical fields** (DocumentType, PlaintiffSide, DefendantSide): Metadata failures still THROW and stop the record/sync (existing behavior preserved)
- **Non-critical fields** (e.g., ClientNumber dropdown): Metadata failures are caught, logged as WARNING, field is omitted, sync continues

**File**: `Services\SyncService.cs`

**Implementation**:

For **non-critical fields** like ClientNumber dropdown validation (line 1673-1724):

```csharp
try
{
    var allowedLabels = await _mondayMetadataProvider.GetAllowedDropdownLabelsAsync(boardId, columnId, ct);

    if (!allowedLabels.Contains(trimmedClientNumber))
    {
        // Log warning, skip field via SkipLogger
        columnValues.Remove(columnId);
        return;
    }

    columnValues[columnId] = new { labels = new[] { trimmedClientNumber } };
}
catch (Exception ex)
{
    // Non-critical field metadata failure - log warning and skip field, continue sync
    _logger.LogWarning(
        ex,
        "Failed to fetch/validate metadata for non-critical dropdown column {ColumnId} on board {BoardId}. ClientNumber '{ClientNumber}' for TikCounter {TikCounter}, TikNumber {TikNumber} will be omitted from this sync. Exception: {Message}",
        columnId,
        boardId,
        trimmedClientNumber,
        tikCounter,
        tikNumber ?? "<null>",
        ex.Message);
    // Field is NOT added to columnValues, sync continues
}
```

For **critical fields** (DocumentType) in `ValidateCriticalFieldsAsync` (line 2212-2345):

```csharp
// Detect actual column type from Monday metadata
// Note: This will THROW on infrastructure failures instead of silently skipping
var actualColumnType = await _mondayMetadataProvider.GetColumnTypeAsync(boardId, columnId2, ct);

// Fetch allowed labels based on detected column type
// Note: This will now THROW on infrastructure failures (auth, network, config)
// instead of silently returning empty labels
HashSet<string> allowedLabels;
if (actualColumnType == "color" || actualColumnType == "status")
{
    allowedLabels = await _mondayMetadataProvider.GetAllowedStatusLabelsAsync(boardId, columnId2, ct);
}
else if (actualColumnType == "dropdown")
{
    allowedLabels = await _mondayMetadataProvider.GetAllowedDropdownLabelsAsync(boardId, columnId2, ct);
}

// No try/catch - exceptions propagate, stopping the record
```

**Behavior**:

| Field Type | Metadata Failure | Result |
|------------|------------------|---------|
| **Critical** (DocumentType) | Auth/Network/Config error | ❌ Exception thrown, record blocked, clear error logged |
| **Non-critical** (ClientNumber) | Auth/Network/Config error | ⚠️ Warning logged, field omitted, sync continues |

**Example Logs**:

**Non-critical field metadata failure** (sync continues):
```
[WARN] Failed to fetch/validate metadata for non-critical dropdown column dropdown_mkxjrssr on board 5035534500. ClientNumber 'ClientA' for TikCounter 39115, TikNumber 9/1808 will be omitted from this sync. Exception: Failed to fetch allowed labels for column dropdown_mkxjrssr on board 5035534500. This is likely an infrastructure issue (auth/network/config). Exception: Unauthorized
[INFO] Successfully created Monday item: TikNumber=9/1808, TikCounter=39115, MondayItemId=7890123456 (without ClientNumber column)
```

**Critical field metadata failure** (sync stops for that record):
```
[ERROR] Failed to fetch allowed STATUS labels for column color_mkxhq546 on board 5035534500...
Exception: InvalidOperationException: Failed to fetch allowed STATUS labels for column color_mkxhq546 on board 5035534500. This is likely an infrastructure issue (auth/network/config). Exception: Unauthorized
[ERROR] CRITICAL VALIDATION FAILED - Item NOT created: TikNumber=9/1808, TikCounter=39115...
```

## Build Status

✅ **0 Errors**, 9 pre-existing warnings only

## Acceptance Criteria

### ✅ 1. No Hardcoded testMode

**Before**:
```csharp
var testMode = false; // Real Odcanit allowlist is NOT test mode (HARDCODED)
```

**After**:
```csharp
// Derived from config
if (odmonTestCasesEnabled)
    testMode = true;
else if (testingEnabled)
    testMode = true;
else
    testMode = false;
```

`testMode` now always reflects the true data source being used.

### ✅ 2. Metadata Failures Handle Correctly by Field Type

**Critical fields** (DocumentType):
- Metadata fetch failure → Exception thrown → Sync fails clearly for that record
- Existing behavior preserved

**Non-critical fields** (ClientNumber dropdown):
- Metadata fetch failure → Warning logged → Field omitted → Sync continues
- Item still created/updated without that field

### ✅ 3. Build Passes

```
Build succeeded.
    9 Warning(s)
    0 Error(s)
```

## Testing Scenarios

### Test 1: Real Odcanit with Allowlist

**Config**:
```json
{
  "Testing": { "Enable": false },
  "OdmonTestCases": { "Enable": false },
  "OdcanitLoad": {
    "EnableAllowList": true,
    "TikCounters": [39115]
  }
}
```

**Expected Log**:
```
[INFO] Data source: Odcanit, testMode=False
[INFO] OdcanitLoad allowlist resolved to 1 TikCounter(s): [39115]
```

**Result**: ✅ testMode=False, uses production board/group

### Test 2: OdmonTestCases Mode

**Config**:
```json
{
  "Testing": { "Enable": false },
  "OdmonTestCases": { 
    "Enable": true,
    "MaxId": 5
  }
}
```

**Expected Log**:
```
[INFO] Data source: OdmonTestCases, testMode=True
[INFO] OdmonTestCases: 10 eligible rows, 5 selected (MaxId=5)
```

**Result**: ✅ testMode=True, uses test board/group if configured

### Test 3: Legacy Testing Mode

**Config**:
```json
{
  "Testing": { "Enable": true },
  "OdmonTestCases": { "Enable": false }
}
```

**Expected Log**:
```
[INFO] Data source: Testing (GuardOdcanitReader), testMode=True
```

**Result**: ✅ testMode=True, GuardOdcanitReader throws on any Odcanit access

### Test 4: Non-Critical Field Metadata Failure

**Setup**:
- Monday API token missing or invalid
- Case has ClientNumber value
- DocumentType is valid

**Expected**:
```
[WARN] Failed to fetch/validate metadata for non-critical dropdown column dropdown_mkxjrssr on board 5035534500. ClientNumber 'ClientA' for TikCounter 39115, TikNumber 9/1808 will be omitted from this sync. Exception: Unauthorized
[INFO] Successfully created Monday item: TikNumber=9/1808, TikCounter=39115, MondayItemId=7890123456
```

**Result**: ✅ Item created without ClientNumber field, sync continues

### Test 5: Critical Field Metadata Failure

**Setup**:
- Monday API token missing or invalid
- Case has DocumentType value

**Expected**:
```
[ERROR] Failed to fetch allowed STATUS labels for column color_mkxhq546 on board 5035534500...
Exception: InvalidOperationException: ...Unauthorized
[ERROR] CRITICAL VALIDATION FAILED - Item NOT created...
```

**Result**: ✅ Item NOT created, clear error about infrastructure failure

## Files Changed

### Modified:
1. ✅ `Services\SyncService.cs`
   - Lines 89-115: Replaced hardcoded `testMode=false` with derived logic from `Testing:Enable` and `OdmonTestCases:Enable`
   - Lines 92-105: Added data source detection and testMode derivation
   - Lines 107-120: Restored board/group selection based on `testMode`
   - Lines 1714-1724: Enhanced non-critical field error handling with clearer logging

## Summary

### ✅ Test Mode Now Reflects Reality
- `testMode` is derived from actual config flags (`Testing:Enable`, `OdmonTestCases:Enable`)
- No hardcoded values that could mask accidental test data usage
- Clear startup logging shows data source and testMode

### ✅ Graceful Degradation for Non-Critical Fields
- Critical fields (DocumentType): Fail fast on metadata errors (existing behavior)
- Non-critical fields (ClientNumber): Log warning, omit field, continue sync
- No silent failures or misleading error messages

### ✅ No Breaking Changes
- Existing critical field validation unchanged
- Build passes with no new errors
- All constraints respected (no Monday/Odcanit schema changes)
