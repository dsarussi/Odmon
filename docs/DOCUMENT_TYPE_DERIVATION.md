# DocumentType Deterministic Derivation

## Overview

**DocumentType does NOT exist in Odcanit DB**. It must be derived deterministically from `ClientVisualID` based on strict business rules.

## Business Rules

### Extraction Logic

**ClientVisualID Format**: `"ClientNumber\OtherData"` or `"ClientNumber/OtherData"`

Examples:
- `"102\5334"` → ClientNumber = `102`
- `"9/1858"` → ClientNumber = `9`
- `"7/1235744"` → ClientNumber = `7`

**Extraction**: Take substring before the first `\` or `/` separator, then parse as integer.

### Mapping Rules

| ClientNumber | DocumentType |
|--------------|--------------|
| 1, 2, 5, 8 | כתב הגנה |
| 6 | מכתב דרישה אילי |
| 4, 7, 9 | כתב תביעה |
| ≥ 100 (any 3+ digit number) | כתב תביעה |

### Error Handling

**All failures throw `InvalidOperationException`** (DocumentType is a critical field):

1. **ClientVisualID is null/empty** → THROW
2. **Cannot extract ClientNumber** (no separator, invalid format) → THROW
3. **Cannot parse ClientNumber to integer** → THROW
4. **ClientNumber doesn't match any rule** (e.g., ClientNumber = 3) → THROW

**No silent defaults. No fallbacks. Deterministic only.**

## Implementation

### Method Signature

**File**: `Services\SyncService.cs`

```csharp
/// <summary>
/// Determines DocumentType from ClientVisualID based on strict business rules.
/// DocumentType does NOT exist in Odcanit DB and must be derived deterministically.
/// </summary>
/// <param name="clientVisualID">ClientVisualID in format "ClientNumber\OtherData", e.g. "102\5334" or "9/1858"</param>
/// <returns>DocumentType status label</returns>
/// <exception cref="InvalidOperationException">Thrown if ClientVisualID is invalid or cannot be parsed (critical field)</exception>
private static string DetermineDocumentTypeFromClientVisualId(string? clientVisualID)
```

### Usage in BuildColumnValuesJsonAsync

**File**: `Services\SyncService.cs` (line ~692)

**Old** (incorrect - expected from DB):
```csharp
// Set document type: use DB value first, fallback to determination by client number
var documentType = c.DocumentType;
if (string.IsNullOrWhiteSpace(documentType))
{
    documentType = DetermineDocumentType(c.ClientVisualID);
}
```

**New** (correct - always derived):
```csharp
// DocumentType does NOT exist in Odcanit DB - must be derived from ClientVisualID
// This is deterministic and based on strict business rules
var documentType = DetermineDocumentTypeFromClientVisualId(c.ClientVisualID);

// Set on case object for critical field validation
c.DocumentType = documentType;

TryAddStatusLabelColumn(columnValues, _mondaySettings.DocumentTypeStatusColumnId, documentType);
```

### Critical Field Validation

**File**: `Services\SyncService.cs` - `ValidateCriticalFieldsAsync`

After DocumentType is derived and set on `c.DocumentType`, the existing critical field validation runs:

1. **Value is null/empty** → `MISSING_VALUE` exception (should never happen now)
2. **Column ID not configured** → `CONFIG_MISSING_COLUMN_ID` exception
3. **Column type not detected** → `METADATA_MISSING_COLUMN_TYPE` exception
4. **Column type unsupported** → `UNSUPPORTED_COLUMN_TYPE` exception
5. **Value not in Monday allowed labels** → `INVALID_LABEL` exception

**DocumentType validation is strict and cannot be skipped.**

## Examples

### Example 1: TikNumber = 9/1858

**Input**:
- `ClientVisualID = "9/1858"`

**Processing**:
1. Extract ClientNumber: `"9"` (substring before `/`)
2. Parse to int: `9`
3. Apply rules: ClientNumber = 9 → **"כתב תביעה"** (Rule 3)

**Result**:
- `c.DocumentType = "כתב תביעה"`
- Monday column `color_mkxhq546` set to `"כתב תביעה"`

**Log**:
```
[DEBUG] DocumentType for TikCounter=39231 derived from ClientVisualID '9/1858': 'כתב תביעה'
```

### Example 2: TikNumber = 7/1235744

**Input**:
- `ClientVisualID = "7/1235744"`

**Processing**:
1. Extract ClientNumber: `"7"` (substring before `/`)
2. Parse to int: `7`
3. Apply rules: ClientNumber = 7 → **"כתב תביעה"** (Rule 3)

**Result**:
- `c.DocumentType = "כתב תביעה"`
- Monday column `color_mkxhq546` set to `"כתב תביעה"`

### Example 3: ClientNumber = 1

**Input**:
- `ClientVisualID = "1\2345"`

**Processing**:
1. Extract ClientNumber: `"1"` (substring before `\`)
2. Parse to int: `1`
3. Apply rules: ClientNumber = 1 → **"כתב הגנה"** (Rule 1)

**Result**:
- `c.DocumentType = "כתב הגנה"`

### Example 4: ClientNumber = 102

**Input**:
- `ClientVisualID = "102\5334"`

**Processing**:
1. Extract ClientNumber: `"102"` (substring before `\`)
2. Parse to int: `102`
3. Apply rules: ClientNumber = 102 (≥ 100) → **"כתב תביעה"** (Rule 4)

**Result**:
- `c.DocumentType = "כתב תביעה"`

### Example 5: ClientNumber = 6

**Input**:
- `ClientVisualID = "6/9876"`

**Processing**:
1. Extract ClientNumber: `"6"` (substring before `/`)
2. Parse to int: `6`
3. Apply rules: ClientNumber = 6 → **"מכתב דרישה אילי"** (Rule 2)

**Result**:
- `c.DocumentType = "מכתב דרישה אילי"`

### Example 6: Error - Invalid ClientNumber

**Input**:
- `ClientVisualID = "ABC/1234"`

**Processing**:
1. Extract ClientNumber: `"ABC"` (substring before `/`)
2. Parse to int: **FAILS**

**Result**:
```
Exception: InvalidOperationException: Cannot determine DocumentType: Failed to parse ClientNumber from ClientVisualID 'ABC/1234'. Extracted: 'ABC'. DocumentType is a critical field.
```

**Behavior**: Item NOT synced, clear error logged.

### Example 7: Error - Unmatched ClientNumber

**Input**:
- `ClientVisualID = "3/5555"`

**Processing**:
1. Extract ClientNumber: `"3"` (substring before `/`)
2. Parse to int: `3`
3. Apply rules: ClientNumber = 3 → **No rule matches**

**Result**:
```
Exception: InvalidOperationException: Cannot determine DocumentType: ClientNumber 3 (from ClientVisualID '3/5555') does not match any known business rule. DocumentType is a critical field.
```

**Behavior**: Item NOT synced, clear error logged.

## Changes Made

### Modified Files

1. ✅ `Services\SyncService.cs`
   - **Line 1762**: Replaced `DetermineDocumentType` with `DetermineDocumentTypeFromClientVisualId`
     - Now extracts ClientNumber from ClientVisualID correctly (substring before `\` or `/`)
     - Implements all 4 business rules
     - Throws on all failures (no null returns)
   - **Line 692**: Updated `BuildColumnValuesJsonAsync`
     - Removed fallback to `c.DocumentType` from DB
     - Always calls `DetermineDocumentTypeFromClientVisualId`
     - Sets `c.DocumentType` for critical validation
     - Always adds to Monday payload

### What Changed

| Before | After |
|--------|-------|
| Expected DocumentType from Odcanit DB | **Never** reads from DB |
| Fallback to determination if DB is empty | **Always** derives from ClientVisualID |
| Parsed entire ClientVisualID as integer | Extracts substring before separator |
| Incomplete rules (only 1, 4, 7, 9, ≥100) | **Complete** rules (1, 2, 4, 5, 6, 7, 8, 9, ≥100) |
| Returned null on failures | **Throws** InvalidOperationException |
| Silent null handling | **Strict** error handling |

## Testing Scenarios

### Test 1: Standard Case (ClientNumber = 9)

**Config**:
```json
{
  "OdcanitLoad": {
    "EnableAllowList": true,
    "TikNumbers": ["9/1858"]
  }
}
```

**Expected**:
```
[INFO] Resolving 1 TikNumber(s) to TikCounters: [9/1858]
[DEBUG] Resolved TikNumber '9/1858' -> TikCounter 39231
[INFO] Loaded 1 cases from Odcanit by TikCounter
[DEBUG] DocumentType for TikCounter=39231 derived from ClientVisualID '9/1858': 'כתב תביעה'
[DEBUG] Critical field validated OK: TikCounter=39231, Field=DocumentType, ColumnType=status, Value='כתב תביעה'
[INFO] Successfully created Monday item: TikNumber=9/1858, TikCounter=39231
```

**Result**: ✅ Item created with DocumentType = "כתב תביעה"

### Test 2: Multiple Cases

**Config**:
```json
{
  "OdcanitLoad": {
    "EnableAllowList": true,
    "TikNumbers": ["9/1858", "7/1235744"]
  }
}
```

**Expected**:
- Case 1 (9/1858): DocumentType = "כתב תביעה"
- Case 2 (7/1235744): DocumentType = "כתב תביעה"

**Result**: ✅ Both items created successfully

### Test 3: Invalid ClientVisualID

**Scenario**: Case has `ClientVisualID = null` or `ClientVisualID = "INVALID"`

**Expected**:
```
[ERROR] Failed to build column values for TikCounter=12345...
Exception: InvalidOperationException: Cannot determine DocumentType: ClientVisualID is null or empty. DocumentType is a critical field and must be derived from ClientVisualID.
```

**Result**: ✅ Item NOT created, clear error explaining the problem

### Test 4: Unsupported ClientNumber

**Scenario**: Case has `ClientVisualID = "3/1234"` (ClientNumber 3 has no mapping rule)

**Expected**:
```
[ERROR] Failed to build column values for TikCounter=12345...
Exception: InvalidOperationException: Cannot determine DocumentType: ClientNumber 3 (from ClientVisualID '3/1234') does not match any known business rule. DocumentType is a critical field.
```

**Result**: ✅ Item NOT created, clear error identifying unsupported ClientNumber

### Test 5: Different ClientNumber Types

| ClientVisualID | ClientNumber | DocumentType | Rule |
|----------------|--------------|--------------|------|
| `"1/1234"` | 1 | כתב הגנה | Rule 1 |
| `"2/5678"` | 2 | כתב הגנה | Rule 1 |
| `"5/9999"` | 5 | כתב הגנה | Rule 1 |
| `"8/1111"` | 8 | כתב הגנה | Rule 1 |
| `"6/2222"` | 6 | מכתב דרישה אילי | Rule 2 |
| `"4/3333"` | 4 | כתב תביעה | Rule 3 |
| `"7/4444"` | 7 | כתב תביעה | Rule 3 |
| `"9/5555"` | 9 | כתב תביעה | Rule 3 |
| `"100/6666"` | 100 | כתב תביעה | Rule 4 |
| `"102\5334"` | 102 | כתב תביעה | Rule 4 |

**Result**: ✅ All cases derive correct DocumentType

## Constraints Respected

✅ **No Monday board/column changes**
✅ **No Odcanit DB schema/view changes**
✅ **No config flags added**
✅ **No silent defaults**
✅ **Deterministic behavior only**
✅ **Strict critical field validation**

## Build Status

✅ **0 Errors**, 9 pre-existing warnings

## Summary

### ✅ DocumentType Always Populated
- Derived from ClientVisualID using strict business rules
- Never read from Odcanit DB (which doesn't have this field)
- Set on `c.DocumentType` for critical validation

### ✅ No More MISSING_VALUE Errors
- DocumentType is always derived (never null)
- Only fails if ClientVisualID is invalid (correct behavior)

### ✅ Validation Remains Strict
- After derivation, critical field validation runs
- Invalid labels still rejected with `INVALID_LABEL`
- Infrastructure failures still surface clearly

### ✅ Deterministic and Auditable
- Same ClientVisualID → Same DocumentType (always)
- Clear logs show derivation: `"derived from ClientVisualID '9/1858': 'כתב תביעה'"`
- Exceptions include full context (ClientVisualID, ClientNumber, reason)
