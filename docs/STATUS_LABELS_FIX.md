# Monday Status Labels Fix & Test Mode Elimination

## Problems Fixed

### Problem 1: Empty AllowedLabels for Status Columns
**Symptom**: `AllowedLabels=[]` for status column `color_mkxhq546`, causing validation to fail for valid values like "כתב תביעה".

**Root Cause**: Status columns were being parsed as dropdowns (array structure) instead of using their actual dictionary structure.

**Fix**: `GetAllowedStatusLabelsAsync` now correctly parses status column labels as dictionary values.

### Problem 2: Silent Failure Hiding Infrastructure Issues
**Symptom**: Monday API errors (auth, network, HTTP failures) were caught and returned as empty label sets, causing misleading validation failures.

**Root Cause**: `try/catch` blocks returned `new HashSet<string>()` on ANY exception, hiding the real issue.

**Fix**: All Monday metadata methods now THROW exceptions on infrastructure failures instead of returning empty sets.

### Problem 3: Test Mode Still Active with Real Odcanit Data
**Symptom**: Logs showed `testMode=True` and synthetic `TikCounter=900xxx` when running with Odcanit allowlist.

**Root Cause**: `testMode` was read from `Safety:TestMode` config even when using real Odcanit allowlist.

**Fix**: `testMode` is now hardcoded to `false` when using Odcanit allowlist. Data source logged at startup.

## Implementation Details

### 1. Status Label Parsing (Fixed Structure)

**File**: `Monday\MondayMetadataProvider.cs`

**Status Column JSON Structure**:
```json
{
  "labels": {
    "0": "כתב תביעה",
    "1": "כתב הגנה",
    "11": "תצהיר עד ראשי",
    "101": "תצהיר מומחה"
  }
}
```

**Correct Parsing** (Dictionary Values):
```csharp
if (settingsRoot.TryGetProperty("labels", out var labelsElement) &&
    labelsElement.ValueKind == JsonValueKind.Object)  // OBJECT, not Array
{
    foreach (var labelProperty in labelsElement.EnumerateObject())
    {
        var labelValue = labelProperty.Value.GetString();  // Extract VALUE
        if (!string.IsNullOrWhiteSpace(labelValue))
        {
            labels.Add(labelValue);
        }
    }
}
```

### 2. Error Handling: Throw on Infrastructure Failures

**File**: `Monday\MondayMetadataProvider.cs`

**Before** (Silent Failure):
```csharp
catch (Exception ex)
{
    _logger.LogError(ex, "Failed to fetch labels...");
    return new HashSet<string>();  // WRONG: Hides the real issue
}
```

**After** (Throw to Surface Issue):
```csharp
catch (Exception ex)
{
    _logger.LogError(ex, "Failed to fetch labels...");
    throw new InvalidOperationException(
        $"Failed to fetch labels for column {columnId} on board {boardId}. " +
        $"This is likely an infrastructure issue (auth/network/config). Exception: {ex.Message}",
        ex);  // CORRECT: Surfaces the real issue
}
```

**Applied to All Error Cases**:
- ❌ Column not found → THROW (config mismatch)
- ❌ Missing `settings_str` → THROW (invalid column)
- ❌ Empty `settings_str` → THROW (data issue)
- ❌ No labels found → THROW (unexpected structure)
- ❌ HTTP request failed → THROW (auth/network)
- ❌ JSON parsing failed → THROW (API change/corruption)

### 3. Test Mode Elimination for Odcanit Allowlist

**File**: `Services\SyncService.cs`

**Before**:
```csharp
var testMode = safetySection.GetValue<bool>("TestMode", false);
// testMode could be true even when loading real Odcanit data
```

**After**:
```csharp
// Determine data source and test mode
var testMode = false; // Real Odcanit allowlist is NOT test mode
var dataSource = "Odcanit (allowlist)";

var safetySection = _config.GetSection("Safety");
var safetyTestMode = safetySection.GetValue<bool>("TestMode", false);
// ... other setup ...

_logger.LogInformation(
    "Data source: {DataSource}, testMode={TestMode}, Safety:TestMode={SafetyTestMode}",
    dataSource,
    testMode,
    safetyTestMode);
```

**Board/Group Selection Simplified**:
```csharp
var boardIdToUse = casesBoardId;
var groupIdToUse = defaultGroupId;

// Safety:TestMode is now IGNORED when using Odcanit allowlist
// testMode is always false for real Odcanit data
```

### 4. No Caching of Failures

**Files**: `Monday\MondayMetadataProvider.cs`

**Caching Rules**:
- ✅ Cache ONLY when `labels.Count > 0` (successful retrieval)
- ❌ Do NOT cache empty results
- ❌ Do NOT cache exceptions
- ✅ Throw instead of caching failures

**Result**: Transient failures don't get permanently cached as "no labels".

## Behavior Changes

### Infrastructure Failures Now Surface Clearly

**Before** (Hidden):
```
[ERROR] Failed to fetch labels for column color_mkxhq546...
[ERROR] CRITICAL VALIDATION FAILED: AllowedLabels=[] <-- Misleading!
```

**After** (Clear):
```
[ERROR] Failed to fetch labels for column color_mkxhq546...
Exception: InvalidOperationException: Failed to fetch allowed STATUS labels for column color_mkxhq546 on board 5035534500. This is likely an infrastructure issue (auth/network/config). Exception: Unauthorized
```

### Test Mode Correctly Disabled

**Before** (Confusing):
```
[INFO] Loaded 1 cases from Odcanit by TikCounter
[INFO] Case 900003 (1810) action=failed_create_validation testMode=True
```

**After** (Clear):
```
[INFO] Data source: Odcanit (allowlist), testMode=False, Safety:TestMode=False
[INFO] Loaded 1 cases from Odcanit by TikCounter
[INFO] Case 39115 (9/1808) action=created testMode=False
```

### Valid Status Values Now Accepted

**Scenario**: DB has `DocumentType="כתב הגנה"`, Monday column has that label.

**Before**:
```
[ERROR] AllowedLabels=[]  <-- Empty due to parsing bug or silent failure
[ERROR] CRITICAL VALIDATION FAILED
```

**After**:
```
[DEBUG] Parsing settings_str for STATUS column color_mkxhq546...
[DEBUG] Found STATUS label for column color_mkxhq546 (key=0): 'כתב תביעה'
[DEBUG] Found STATUS label for column color_mkxhq546 (key=1): 'כתב הגנה'
[INFO] Resolved 4 allowed STATUS label(s) for column color_mkxhq546 on board 5035534500: [כתב תביעה, כתב הגנה, תצהיר עד ראשי, תצהיר מומחה]
[DEBUG] Critical field validated OK: TikCounter=39115, Field=DocumentType, ColumnType=status, Value='כתב הגנה'
```

## Configuration Changes

### appsettings.json

```json
{
  "OdcanitLoad": {
    "EnableAllowList": false,
    "TikCounters": [],
    "TikNumbers": []
  },
  "Testing": {
    "Enable": false,  // DISABLED - no synthetic test data
    "TikCounters": []  // EMPTY - no synthetic TikCounters
  },
  "OdmonTestCases": {
    "Enable": false  // DISABLED - no synthetic test data
  }
}
```

### To Use Real Odcanit with Allowlist

```json
{
  "OdcanitLoad": {
    "EnableAllowList": true,
    "TikCounters": [39115, 42020],
    "TikNumbers": ["9/1808"]
  },
  "Testing": {
    "Enable": false  // MUST be false
  }
}
```

## Error Messages Guide

### Infrastructure Failures (Now Thrown)

**Missing Monday API Token**:
```
System.InvalidOperationException: Failed to fetch allowed STATUS labels for column color_mkxhq546 on board 5035534500. This is likely an infrastructure issue (auth/network/config). Exception: Unauthorized
```
**Action**: Configure `Monday__ApiToken` environment variable or Key Vault secret.

**Network Timeout**:
```
System.InvalidOperationException: Failed to fetch allowed STATUS labels for column color_mkxhq546 on board 5035534500. This is likely an infrastructure issue (auth/network/config). Exception: The operation has timed out
```
**Action**: Check network connectivity to Monday API.

**Column Not Found**:
```
System.InvalidOperationException: Status column color_mkxhq546 not found on board 5035534500. This indicates a configuration mismatch. Available columns: [text_mkwe19hn, color_mkxhq546, ...]
```
**Action**: Verify column ID in `MondaySettings` matches actual Monday board.

### Data Validation Failures (Still Block Record Only)

**Invalid Label Value**:
```
[ERROR] CRITICAL FIELD VALIDATION FAILED: TikCounter=39115, TikNumber=9/1808, Field=DocumentType, ColumnId=color_mkxhq546, ColumnType=status, Value='מסמך לא ידוע', Reason=INVALID_LABEL, AllowedLabels=[כתב תביעה, כתב הגנה, תצהיר עד ראשי, תצהיר מומחה]
```
**Action**: Fix data in Odcanit DB or add label to Monday column.

**Missing Value**:
```
[ERROR] CRITICAL FIELD VALIDATION FAILED: TikCounter=39115, TikNumber=9/1808, Field=DocumentType, ColumnId=color_mkxhq546, Value=<null/empty>, Reason=MISSING_VALUE
```
**Action**: Fix data in Odcanit DB (DocumentType is required).

## Testing

### Test 1: Valid Status Value

**Setup**:
- `OdcanitLoad:EnableAllowList=true`
- `TikCounters=[39115]`
- DB has `DocumentType="כתב הגנה"`
- Monday has label "כתב הגנה"

**Expected Logs**:
```
[INFO] Data source: Odcanit (allowlist), testMode=False, Safety:TestMode=False
[INFO] OdcanitLoad allowlist resolved to 1 TikCounter(s): [39115]
[INFO] Loaded 1 cases from Odcanit by TikCounter
[DEBUG] Detected column type for critical field DocumentType (ColumnId=color_mkxhq546): status
[INFO] Resolved 4 allowed STATUS label(s) for column color_mkxhq546 on board 5035534500: [כתב תביעה, כתב הגנה, תצהיר עד ראשי, תצהיר מומחה]
[DEBUG] Critical field validated OK: TikCounter=39115, Field=DocumentType, ColumnType=status, Value='כתב הגנה'
[INFO] Successfully created Monday item: TikNumber=9/1808, TikCounter=39115, MondayItemId=...
```

**Result**: ✅ Item created with correct DocumentType.

### Test 2: Infrastructure Failure (Missing Token)

**Setup**:
- Monday API token missing or invalid

**Expected**:
```
[ERROR] Monday API token not found or is placeholder...
[ERROR] Failed to fetch allowed STATUS labels for column color_mkxhq546 on board 5035534500...
Exception: InvalidOperationException: Failed to fetch allowed STATUS labels for column color_mkxhq546 on board 5035534500. This is likely an infrastructure issue (auth/network/config). Exception: Unauthorized
```

**Result**: ✅ Clear error identifying the infrastructure issue, not a silent empty label set.

### Test 3: Invalid DocumentType

**Setup**:
- `OdcanitLoad:EnableAllowList=true`
- DB has `DocumentType="מסמך לא ידוע"` (not in Monday labels)

**Expected**:
```
[INFO] Resolved 4 allowed STATUS label(s) for column color_mkxhq546 on board 5035534500: [כתב תביעה, כתב הגנה, תצהיר עד ראשי, תצהיר מומחה]
[ERROR] CRITICAL FIELD VALIDATION FAILED: TikCounter=39115, TikNumber=9/1808, Field=DocumentType, ColumnId=color_mkxhq546, ColumnType=status, Value='מסמך לא ידוע', Reason=INVALID_LABEL, AllowedLabels=[כתב תביעה, כתב הגנה, תצהיר עד ראשי, תצהיר מומחה]
[ERROR] CRITICAL VALIDATION FAILED - Item NOT created...
```

**Result**: ✅ Only that record blocked, other cases continue.

## Files Changed

### Modified:
1. ✅ `Monday\MondayMetadataProvider.cs`
   - Fixed status label parsing (dictionary values)
   - Changed all error paths to THROW instead of return empty
   - No caching of failures

2. ✅ `Services\SyncService.cs`
   - `testMode` hardcoded to `false` for Odcanit allowlist
   - Added data source logging at startup
   - Removed Safety:TestMode logic for allowlist mode
   - Removed try/catch around metadata calls (let exceptions propagate)

3. ✅ `OdcanitAccess\SqlOdcanitReader.cs`
   - Fixed `ResolveTikNumbersToCountersAsync` with explicit SQL parameterization

4. ✅ `appsettings.json` / `appsettings.Development.json`
   - `Testing:Enable=false`
   - `OdmonTestCases:Enable=false`
   - Added `OdcanitLoad` section

## Summary

### ✅ Status Labels Load Correctly
- Status columns (type="color" or "status") parse labels as dictionary values
- Monday API returns labels successfully
- AllowedLabels contains actual labels like ["כתב תביעה", "כתב הגנה", ...]

### ✅ Infrastructure Failures Surface Clearly
- Auth failures → `InvalidOperationException` with "Unauthorized"
- Network failures → `InvalidOperationException` with timeout message
- Config mismatches → `InvalidOperationException` with available columns
- No more silent empty label sets hiding the real issue

### ✅ Test Mode Properly Disabled
- `testMode=False` when using Odcanit allowlist
- No synthetic TikCounters (900xxx)
- Real TikCounters from Odcanit (39115, 42020, etc.)
- Data source logged: "Odcanit (allowlist)"

### ✅ Critical Field Validation Works
- Valid values in DB that exist in Monday labels → PASS
- Invalid values in DB → FAIL that record only (INVALID_LABEL)
- Missing values in DB → FAIL that record only (MISSING_VALUE)
- Infrastructure failures → Exception propagates (don't block all records silently)

### ✅ Build Status
- 0 Errors
- 9 Warnings (pre-existing nullable warnings only)

## Acceptance Criteria Met

✅ **Criterion 1**: For `boardId=5035534500` and `columnId=color_mkxhq546` (status), `AllowedLabels` contains "כתב תביעה" and "כתב הגנה"

✅ **Criterion 2**: If Monday API token is missing/invalid or HTTP fails, we see a clear ERROR with details and the exception is THROWN (not swallowed)

✅ **Criterion 3**: Running with `OdcanitLoad:EnableAllowList=true` loads real TikCounters (not 900xxx) and `testMode=False`

✅ **Criterion 4**: DocumentType validation:
   - DB provides "כתב הגנה" and it exists in AllowedLabels → ✅ Passes
   - DB provides invalid/missing → ❌ Fail that record only
