# Critical Field Validation (Fail-Fast)

## Overview

Implemented fail-fast validation for critical Monday columns to prevent incorrect data from being automatically created. This ensures that critical fields like DocumentType, PlaintiffSide, and DefendantSide are properly validated before any Monday item is created or updated.

## Problem Solved

**Before**: System could create Monday items with incorrect critical data (e.g., "כתב תביעה" instead of "כתב הגנה"), leading to wrong document types or party designations being automatically generated.

**After**: System validates critical fields against Monday column metadata BEFORE creating/updating items, throwing controlled exceptions when validation fails.

## Implementation

### 1. CriticalFieldValidationException

**File**: `Exceptions\CriticalFieldValidationException.cs`

Custom exception that captures all relevant context:
- `TikCounter`
- `TikNumber`
- `ColumnId`
- `FieldValue`
- `ValidationReason`

### 2. Critical Columns Defined

**File**: `Services\SyncService.cs`

```csharp
// NOTE: Column type is now detected dynamically from Monday metadata, not hardcoded
private static readonly List<CriticalColumnDefinition> CriticalColumns = new()
{
    new CriticalColumnDefinition
    {
        FieldName = "DocumentType",
        GetValue = c => c.DocumentType,
        ValidationMessage = "Document type (סוג מסמך) is critical - prevents automatic creation of wrong document types (e.g., 'כתב תביעה' vs 'כתב הגנה')"
    },
    new CriticalColumnDefinition
    {
        FieldName = "PlaintiffSide",
        GetValue = c => c.PlaintiffSideRaw,
        ValidationMessage = "Plaintiff side (צד תובע) is critical - prevents incorrect party designation"
    },
    new CriticalColumnDefinition
    {
        FieldName = "DefendantSide",
        GetValue = c => c.DefendantSideRaw,
        ValidationMessage = "Defendant side (צד נתבע) is critical - prevents incorrect party designation"
    }
};
```

**Key Change**: Column types are NO LONGER hardcoded. They are detected dynamically from Monday metadata at validation time.

### 3. Validation Logic

**Method**: `ValidateCriticalFieldsAsync()`

**Validations Performed**:

1. **NULL/Empty Check**: Field value must not be NULL or whitespace
2. **Column Type Detection**: Fetch actual column type from Monday metadata (e.g., "color", "status", "dropdown")
3. **Label Resolution**: Use correct method based on detected type:
   - **Status columns** (type = "color" or "status") → `GetAllowedStatusLabelsAsync()`
   - **Dropdown columns** (type = "dropdown") → `GetAllowedDropdownLabelsAsync()`
4. **Label Existence Check**: Field value must exist in Monday column metadata labels

**Called Before**:
- `CreateMondayItemAsync()` - Before creating new items
- `UpdateMondayItemAsync()` - Before updating existing items (only when `requiresDataUpdate=true`)

**Key Feature**: Column type is detected dynamically from Monday API, not hardcoded. This ensures correct validation method is used (status vs dropdown labels).

### 4. Enhanced Monday Metadata Provider

**Files**: 
- `Monday\IMondayMetadataProvider.cs`
- `Monday\MondayMetadataProvider.cs`

**Added Methods**:

1. **`GetAllowedStatusLabelsAsync()`**: Fetches allowed labels for status columns from Monday API (uses same underlying structure as dropdowns)

2. **`GetColumnTypeAsync()`**: NEW - Detects actual column type from Monday metadata
   - Returns column type string (e.g., "color", "status", "dropdown", "text", etc.)
   - Cached per (boardId, columnId) - no TTL (permanent cache)
   - Used to determine which label validation method to call

**Column Type Detection**:
```csharp
var columnType = await _mondayMetadataProvider.GetColumnTypeAsync(boardId, columnId, ct);
// Returns: "color" for status columns, "dropdown" for dropdowns, etc.
```

### 5. Exception Handling

**Create Flow**:
```csharp
try
{
    mondayIdForLog = await CreateMondayItemAsync(c, caseBoardId, caseGroupId!, itemName, testMode, ct);
    created++;
}
catch (CriticalFieldValidationException critEx)
{
    action = "failed_create_validation";
    failed++;
    errorMessage = critEx.ValidationReason;
    
    _logger.LogError(
        "CRITICAL VALIDATION FAILED - Item NOT created: TikNumber={TikNumber}, TikCounter={TikCounter}, BoardId={BoardId}, ColumnId={ColumnId}, Value='{Value}', Reason={Reason}",
        c.TikNumber, c.TikCounter, caseBoardId, critEx.ColumnId, critEx.FieldValue ?? "<null>", critEx.ValidationReason);
    
    // Continue processing other cases
    continue;
}
```

**Update Flow**: Similar exception handling with `failed_update_validation` action.

## Behavior

### When Critical Field is NULL/Empty

**Example**: `DocumentType` (סוג מסמך) is NULL

**Result**:
- ❌ Monday item NOT created/updated
- ❌ Exception thrown: `CriticalFieldValidationException`
- ✅ ERROR logged with full context
- ✅ Case marked as `failed_create_validation` or `failed_update_validation`
- ✅ Other cases continue processing

**Log Output**:
```
[ERROR] CRITICAL FIELD VALIDATION FAILED: TikCounter=900000, TikNumber=5-1-1808, Field=DocumentType, ColumnId=color_mkxhq546, Value=<null/empty>, Reason=MISSING_VALUE. Document type (סוג מסמך) is critical - prevents automatic creation of wrong document types (e.g., 'כתב תביעה' vs 'כתב הגנה')

[ERROR] CRITICAL VALIDATION FAILED - Item NOT created: TikNumber=5-1-1808, TikCounter=900000, BoardId=5035534500, ColumnId=color_mkxhq546, Value='<null>', Reason=MISSING_VALUE - Document type (סוג מסמך) is critical...
```

### When Critical Field Value is Invalid

**Example**: `DocumentType` = "מסמך לא ידוע" (not in Monday labels)

**Result**:
- ❌ Monday item NOT created/updated
- ❌ Exception thrown: `CriticalFieldValidationException`
- ✅ ERROR logged with field value and all allowed labels
- ✅ Case marked as `failed_create_validation` or `failed_update_validation`
- ✅ Other cases continue processing

**Log Output**:
```
[ERROR] CRITICAL FIELD VALIDATION FAILED: TikCounter=900000, TikNumber=5-1-1808, Field=DocumentType, ColumnId=color_mkxhq546, Value='מסמך לא ידוע', Reason=INVALID_LABEL, AllowedLabels=[כתב תביעה, כתב הגנה, תצהיר עד ראשי, תצהיר מומחה]. Document type (סוג מסמך) is critical - prevents automatic creation of wrong document types (e.g., 'כתב תביעה' vs 'כתב הגנה')

[ERROR] CRITICAL VALIDATION FAILED - Item NOT created: TikNumber=5-1-1808, TikCounter=900000, BoardId=5035534500, ColumnId=color_mkxhq546, Value='מסמך לא ידוע', Reason=INVALID_LABEL - Value 'מסמך לא ידוע' not in allowed labels: [כתב תביעה, כתב הגנה, תצהיר עד ראשי, תצהיר מומחה]...
```

### When Critical Field is Valid

**Example**: `DocumentType` = "כתב תביעה" (exists in Monday status labels)

**Result**:
- ✅ Column type detected from Monday metadata (e.g., "color")
- ✅ Correct validation method selected (status labels)
- ✅ Validation passes
- ✅ Monday item created/updated normally
- ✅ DEBUG logs show column type detection and validation success

**Log Output**:
```
[DEBUG] Detected column type for critical field DocumentType (ColumnId=color_mkxhq546): color
[DEBUG] Critical field validated OK: TikCounter=900000, Field=DocumentType, ColumnType=color, Value='כתב תביעה'
```

### Why Column Type Detection Matters

**Problem Before**: If a critical field was hardcoded as "dropdown" but the Monday column was actually "status" (type="color"), validation would fail for valid values like "כתב הגנה" or "כתב תביעה".

**Solution Now**: System detects actual column type from Monday metadata:
- **Status columns** (Monday type = "color") → Use status label validation
- **Dropdown columns** (Monday type = "dropdown") → Use dropdown label validation

**Result**: Valid values like "כתב הגנה" for status columns now pass validation correctly.

## Critical Columns Configuration

### Current Critical Columns

| Field Name | Monday Column ID | Column Type (Detected) | Validation Message |
|------------|------------------|------------------------|-------------------|
| `DocumentType` | `color_mkxhq546` | Auto-detected from Monday | Prevents automatic creation of wrong document types |
| `PlaintiffSide` | `color_mkxh8gsq` | Auto-detected from Monday | Prevents incorrect party designation (צד תובע) |
| `DefendantSide` | `color_mkxh5x31` | Auto-detected from Monday | Prevents incorrect party designation (צד נתבע) |

**Note**: Column types are detected dynamically at runtime from Monday metadata, not hardcoded.

### Adding New Critical Columns

To add a new critical column, update `CriticalColumns` list in `SyncService.cs`:

```csharp
private static readonly List<CriticalColumnDefinition> CriticalColumns = new()
{
    // ... existing columns ...
    new CriticalColumnDefinition
    {
        FieldName = "NewFieldName",
        // NO ColumnType - it's auto-detected from Monday metadata
        GetValue = c => c.NewFieldProperty,
        ValidationMessage = "Explanation of why this field is critical"
    }
};
```

Then add column ID mapping in `GetColumnIdForField()`:

```csharp
private string? GetColumnIdForField(string fieldName)
{
    return fieldName switch
    {
        // ... existing mappings ...
        "NewFieldName" => "column_id_here",
        _ => null
    };
}
```

**Important**: You do NOT need to specify the column type. The system will:
1. Detect the column type from Monday metadata at runtime
2. Select the correct validation method (status vs dropdown labels)
3. Validate against the appropriate label set

## No Silent Defaults

### Enforced Rules

1. ❌ **NO** silent defaults for critical fields
2. ❌ **NO** fallback values for missing critical fields
3. ❌ **NO** automatic creation when critical validation fails
4. ✅ **ALWAYS** fail fast with clear error message
5. ✅ **ALWAYS** log full context (TikCounter, TikNumber, ColumnId, Value, Reason)

### Example of What's Prevented

**BAD** (Before):
```csharp
var documentType = c.DocumentType ?? "כתב תביעה"; // Silent default!
```

**GOOD** (After):
```csharp
// NO default - validation will fail if NULL
await ValidateCriticalFieldsAsync(boardId, c, ct);
// If we get here, DocumentType is guaranteed to be valid
```

## Error Handling

### Graceful Degradation

- ✅ If Monday API is unavailable → Log warning, skip validation (don't block all syncs)
- ✅ If column ID not configured → Log warning, skip that field's validation
- ✅ If validation fails for one case → Log error, mark as failed, continue with other cases
- ❌ Never create/update Monday item when critical validation fails

### Production Safety

- Validation runs in both **CREATE** and **UPDATE** flows
- Validation runs in both **TEST** and **PRODUCTION** modes
- No performance impact: metadata is cached (15-minute TTL)
- Other cases continue processing even if one fails validation

## Testing

### Test with NULL Critical Field

1. **Prepare**: Row in `dbo.OdmonTestCases` with `NULL` in `סוג_מסמך`
2. **Run**: `dotnet run`
3. **Expected**:
   ```
   [ERROR] CRITICAL FIELD VALIDATION FAILED: ... Value=<null/empty>, Reason=MISSING_VALUE...
   [ERROR] CRITICAL VALIDATION FAILED - Item NOT created...
   ```
4. **Verify**: Monday item NOT created, case marked as `failed_create_validation`

### Test with Invalid Critical Field Value

1. **Prepare**: Row with `סוג_מסמך` = "מסמך לא ידוע" (not in Monday labels)
2. **Run**: `dotnet run`
3. **Expected**:
   ```
   [ERROR] CRITICAL FIELD VALIDATION FAILED: ... Value='מסמך לא ידוע', Reason=INVALID_LABEL, AllowedLabels=[...]...
   ```
4. **Verify**: Monday item NOT created, error shows all allowed labels

### Test with Valid Critical Field Value

1. **Prepare**: Row with `סוג_מסמך` = "כתב תביעה" (valid Monday label)
2. **Run**: `dotnet run`
3. **Expected**:
   ```
   [DEBUG] Critical field validated OK: ... Field=DocumentType, Value='כתב תביעה'
   [INFO] Successfully created Monday item...
   ```
4. **Verify**: Monday item created successfully

## Benefits

✅ **Prevents Data Errors**: No more wrong document types automatically created  
✅ **Clear Feedback**: Detailed error messages show exactly what's wrong and why  
✅ **Fast Failure**: Fails before Monday API call, saving time and API quota  
✅ **Complete Context**: Logs include all relevant info for debugging (including detected column type)  
✅ **Graceful**: Other cases continue processing even if one fails  
✅ **No Silent Bugs**: No hidden defaults that mask data quality issues  
✅ **Extensible**: Easy to add new critical columns  
✅ **Production Safe**: Cached metadata, error handling, no blocking behavior  
✅ **Automatic Type Detection**: No need to hardcode column types - system detects from Monday  
✅ **Correct Validation**: Uses status labels for status columns, dropdown labels for dropdown columns  
✅ **Accepts Valid Values**: Values like "כתב הגנה" or "כתב תביעה" pass validation for status columns  

## Monitoring

### Key Metrics to Monitor

- **Failed validations**: Count of `failed_create_validation` and `failed_update_validation`
- **Validation reasons**: Distribution of `MISSING_VALUE` vs `INVALID_LABEL`
- **Most common invalid values**: Which values are being rejected?
- **Column-specific failures**: Which critical columns fail most often?

### Log Queries

**Count validation failures**:
```
"CRITICAL VALIDATION FAILED" | count by Reason
```

**Find invalid values for specific column**:
```
"CRITICAL VALIDATION FAILED" AND "ColumnId=color_mkxhq546" | fields Value, AllowedLabels
```

**Cases that need data correction**:
```
"failed_create_validation" OR "failed_update_validation" | fields TikCounter, TikNumber, ColumnId, Value
```
