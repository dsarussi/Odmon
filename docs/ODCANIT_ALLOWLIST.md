# Odcanit Allowlist Feature

## Overview

The **Odcanit Allowlist** feature allows you to load ONLY specific cases from Odcanit DB (not synthetic test data) by explicitly configuring which cases to sync. This replaces the previous IntegrationDb test-cases flow and ensures you're working with real Odcanit data.

## Configuration

### appsettings.json / appsettings.Development.json

```json
{
  "OdcanitLoad": {
    "EnableAllowList": false,
    "TikCounters": [],
    "TikNumbers": []
  },
  "Testing": {
    "Enable": false,
    "Source": "IntegrationDbOdmonTestCases",
    "TikCounters": [],
    "TableName": "dbo.OdmonTestCases"
  },
  "OdmonTestCases": {
    "Enable": false,
    "MaxId": null,
    "OnlyIds": null
  }
}
```

### Configuration Options

#### `OdcanitLoad:EnableAllowList` (bool)
- **`true`**: Load ONLY cases specified in `TikCounters` and `TikNumbers` allowlists
- **`false`**: Use production behavior (`Sync:TikCounters` configuration)
- **Default**: `false`

#### `OdcanitLoad:TikCounters` (int[])
- List of TikCounter values (internal integer IDs) to load
- Example: `[39115, 42020]`
- **Default**: `[]` (empty)

#### `OdcanitLoad:TikNumbers` (string[])
- List of TikNumber values (case numbers like "9/1808") to load
- These will be resolved to TikCounters via Odcanit DB lookup
- Example: `["9/1808", "5/1810"]`
- **Default**: `[]` (empty)

#### `Testing:Enable` (bool)
- **MUST BE `false`** to use real Odcanit data
- When `true`, uses IntegrationDb test cases (deprecated flow)
- **Default**: `false`

#### `OdmonTestCases:Enable` (bool)
- **MUST BE `false`** to avoid synthetic test data
- **Default**: `false`

## Usage Examples

### Example 1: Load Two Specific Cases by TikCounter

```json
{
  "OdcanitLoad": {
    "EnableAllowList": true,
    "TikCounters": [39115, 42020],
    "TikNumbers": []
  }
}
```

**Result**: Loads cases with `TikCounter=39115` and `TikCounter=42020` from Odcanit DB.

### Example 2: Load Cases by TikNumber

```json
{
  "OdcanitLoad": {
    "EnableAllowList": true,
    "TikCounters": [],
    "TikNumbers": ["9/1808", "5/1810"]
  }
}
```

**Result**:
1. Queries Odcanit DB to resolve `"9/1808"` → TikCounter (e.g., 39115)
2. Queries Odcanit DB to resolve `"5/1810"` → TikCounter (e.g., 42020)
3. Loads cases with those TikCounters

### Example 3: Mixed - TikCounters + TikNumbers

```json
{
  "OdcanitLoad": {
    "EnableAllowList": true,
    "TikCounters": [39115],
    "TikNumbers": ["5/1810"]
  }
}
```

**Result**: Loads case `TikCounter=39115` directly, PLUS resolves `"5/1810"` to its TikCounter and loads it.

### Example 4: Production Mode (No Allowlist)

```json
{
  "OdcanitLoad": {
    "EnableAllowList": false,
    "TikCounters": [],
    "TikNumbers": []
  },
  "Sync": {
    "TikCounters": [39115, 42020, 43000]
  }
}
```

**Result**: Ignores `OdcanitLoad` settings, uses `Sync:TikCounters` instead.

## Behavior

### When `EnableAllowList=true`

1. **TikCounter Resolution**:
   - Start with `OdcanitLoad:TikCounters` list
   - For each `TikNumber` in `OdcanitLoad:TikNumbers`:
     - Query Odcanit DB: `SELECT TikCounter FROM Cases WHERE TikNumber = @tikNumber`
     - Add resolved `TikCounter` to allowlist
     - Log warning if TikNumber cannot be resolved

2. **Validation (Fail-Fast)**:
   - If allowlist is empty after resolution → **ERROR** and stop
   - Error message includes configured values for debugging

3. **Data Loading**:
   - Call `IOdcanitReader.GetCasesByTikCountersAsync(allowlist)`
   - Load cases from **real Odcanit DB** (not IntegrationDb test data)
   - Full enrichment pipeline (clients, sides, diary events, etc.)

### When `EnableAllowList=false`

- Uses `Sync:TikCounters` configuration (production behavior)
- No allowlist filtering
- Still loads from real Odcanit DB (not test data)

## Logging

### Startup Logs

```
[INFO] OdcanitLoad allowlist ENABLED
[INFO] Resolving 2 TikNumber(s) to TikCounters
[DEBUG] Resolved TikNumber '9/1808' -> TikCounter 39115
[DEBUG] Resolved TikNumber '5/1810' -> TikCounter 42020
[INFO] Resolved 2 of 2 TikNumbers
[INFO] OdcanitLoad allowlist resolved to 2 TikCounter(s): [39115, 42020]
[INFO] Loaded 2 cases from Odcanit by TikCounter
```

### Warning Logs (Unresolved TikNumber)

```
[WARN] TikNumber '999/9999' could not be resolved to a TikCounter in Odcanit DB
[INFO] Resolved 1 of 2 TikNumbers
```

### Error Logs (Empty Allowlist)

```
[ERROR] OdcanitLoad:EnableAllowList=true but allowlist is EMPTY after resolution. Configured TikCounters=0, TikNumbers=1. FAIL FAST: At least one case must be specified in the allowlist.
[ERROR] No TikCounters to load. Worker stopped.
```

## Implementation Details

### Files Created/Modified

#### New Files:
- `Configuration\OdcanitLoadOptions.cs` - Configuration model
- `Services\SyncService_AllowList.cs` - Allowlist logic (partial class)
- `docs\ODCANIT_ALLOWLIST.md` - This documentation

#### Modified Files:
- `OdcanitAccess\IOdcanitReader.cs` - Added `ResolveTikNumbersToCountersAsync`
- `OdcanitAccess\SqlOdcanitReader.cs` - Implemented TikNumber resolution
- `OdcanitAccess\GuardOdcanitReader.cs` - Added stub implementation
- `OdcanitAccess\MockOdcanitReader.cs` - Added mock implementation
- `Services\SyncService.cs` - Made partial, updated to use allowlist
- `Program.cs` - Registered `OdcanitLoadOptions`
- `appsettings.json` - Added `OdcanitLoad` section, disabled `Testing` and `OdmonTestCases`
- `appsettings.Development.json` - Same changes as appsettings.json

### Key Methods

#### `IOdcanitReader.ResolveTikNumbersToCountersAsync`

```csharp
Task<Dictionary<string, int>> ResolveTikNumbersToCountersAsync(
    IEnumerable<string> tikNumbers, 
    CancellationToken ct);
```

**Purpose**: Resolve TikNumber strings to TikCounter integers via Odcanit DB lookup.

**Returns**: Dictionary mapping TikNumber → TikCounter for successfully resolved cases.

#### `SyncService.DetermineTikCountersToLoadAsync`

```csharp
private async Task<int[]> DetermineTikCountersToLoadAsync(CancellationToken ct)
```

**Purpose**: Determines which TikCounters to load based on configuration.

**Logic**:
- If `EnableAllowList=true`: Build allowlist from TikCounters + resolved TikNumbers
- If `EnableAllowList=false`: Use `Sync:TikCounters`
- Fail-fast if result is empty

## Testing

### Test Case 1: Allowlist with TikCounters

**Setup**:
```json
{
  "OdcanitLoad": {
    "EnableAllowList": true,
    "TikCounters": [39115],
    "TikNumbers": []
  }
}
```

**Expected**:
- Loads 1 case from Odcanit (TikCounter=39115)
- No test data from IntegrationDb
- Full enrichment applied

**Verification**:
```
dotnet run
```

Look for logs:
```
[INFO] OdcanitLoad allowlist resolved to 1 TikCounter(s): [39115]
[INFO] Loaded 1 cases from Odcanit by TikCounter
```

### Test Case 2: Allowlist with TikNumbers

**Setup**:
```json
{
  "OdcanitLoad": {
    "EnableAllowList": true,
    "TikCounters": [],
    "TikNumbers": ["9/1808"]
  }
}
```

**Expected**:
- Resolves "9/1808" to TikCounter (e.g., 39115)
- Loads that case from Odcanit
- No test data from IntegrationDb

**Verification**:
```
dotnet run
```

Look for logs:
```
[INFO] Resolving 1 TikNumber(s) to TikCounters
[DEBUG] Resolved TikNumber '9/1808' -> TikCounter 39115
[INFO] OdcanitLoad allowlist resolved to 1 TikCounter(s): [39115]
```

### Test Case 3: Empty Allowlist (Fail-Fast)

**Setup**:
```json
{
  "OdcanitLoad": {
    "EnableAllowList": true,
    "TikCounters": [],
    "TikNumbers": []
  }
}
```

**Expected**:
- ERROR logged
- Worker stops immediately

**Verification**:
```
dotnet run
```

Look for logs:
```
[ERROR] OdcanitLoad:EnableAllowList=true but allowlist is EMPTY after resolution. Configured TikCounters=0, TikNumbers=0. FAIL FAST: At least one case must be specified in the allowlist.
[ERROR] No TikCounters to load. Worker stopped.
```

### Test Case 4: Unresolved TikNumber (Warning)

**Setup**:
```json
{
  "OdcanitLoad": {
    "EnableAllowList": true,
    "TikCounters": [],
    "TikNumbers": ["999/9999", "9/1808"]
  }
}
```

**Expected**:
- Warning for "999/9999" (doesn't exist)
- Success for "9/1808"
- Loads the one valid case

**Verification**:
```
[WARN] TikNumber '999/9999' could not be resolved to a TikCounter in Odcanit DB
[INFO] Resolved 1 of 2 TikNumbers
```

## Migration from Test Data

### Old Behavior (Test Data)
```json
{
  "Testing": {
    "Enable": true,
    "Source": "IntegrationDbOdmonTestCases",
    "TikCounters": [900001, 900002, 900003, 900004]
  }
}
```

### New Behavior (Real Odcanit with Allowlist)
```json
{
  "OdcanitLoad": {
    "EnableAllowList": true,
    "TikCounters": [39115, 42020],
    "TikNumbers": []
  },
  "Testing": {
    "Enable": false
  }
}
```

**Key Changes**:
- `Testing:Enable` MUST be `false`
- Data loaded from **real Odcanit DB**, not IntegrationDb
- TikCounters are real production values, not synthetic (900xxx)

## Troubleshooting

### Issue: "No TikCounters to load. Worker stopped."

**Cause**: Allowlist is empty.

**Solutions**:
1. Check `OdcanitLoad:TikCounters` and `OdcanitLoad:TikNumbers` are not both empty
2. If using `TikNumbers`, check they exist in Odcanit DB
3. Set `OdcanitLoad:EnableAllowList=false` to use `Sync:TikCounters` instead

### Issue: "TikNumber 'X' could not be resolved"

**Cause**: TikNumber doesn't exist in Odcanit `Cases` table.

**Solutions**:
1. Verify the TikNumber format (e.g., "9/1808", not "9-1808")
2. Query Odcanit DB directly: `SELECT * FROM Cases WHERE TikNumber = '9/1808'`
3. Use TikCounter directly instead of TikNumber

### Issue: Still loading test data from IntegrationDb

**Cause**: `Testing:Enable=true` or `OdmonTestCases:Enable=true`

**Solution**: Set both to `false` in appsettings.json.

## Best Practices

1. **Start Small**: Test with 1-2 cases before scaling up
2. **Use TikCounters**: More reliable than TikNumbers (no resolution needed)
3. **Check Logs**: Always verify allowlist resolution logs match expectations
4. **Fail-Fast**: If allowlist is empty, worker stops immediately (expected behavior)
5. **No Test Data**: Ensure `Testing:Enable=false` and `OdmonTestCases:Enable=false`

## Summary

✅ **Loads real Odcanit data** (not synthetic test cases)  
✅ **Explicit allowlist** (no surprises about which cases are processed)  
✅ **Supports TikCounter and TikNumber** (flexible configuration)  
✅ **Fail-fast validation** (empty allowlist → immediate error)  
✅ **Full enrichment** (clients, sides, diary, etc. all applied)  
✅ **Clear logging** (resolution, warnings, errors all logged)  
✅ **Production-safe** (allowlist OFF by default)  
