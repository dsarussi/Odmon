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
private static readonly List<CriticalColumnDefinition> CriticalColumns = new()
{
    new CriticalColumnDefinition
    {
        FieldName = "DocumentType",
        ColumnType = "status",
        GetValue = c => c.DocumentType,
        ValidationMessage = "Document type (סוג מסמך) is critical - prevents automatic creation of wrong document types (e.g., 'כתב תביעה' vs 'כתב הגנה')"
    },
    new CriticalColumnDefinition
    {
        FieldName = "PlaintiffSide",
        ColumnType = "status",
        GetValue = c => c.PlaintiffSideRaw,
        ValidationMessage = "Plaintiff side (צד תובע) is critical - prevents incorrect party designation"
    },
    new CriticalColumnDefinition
    {
        FieldName = "DefendantSide",
        ColumnType = "status",
        GetValue = c => c.DefendantSideRaw,
        ValidationMessage = "Defendant side (צד נתבע) is critical - prevents incorrect party designation"
    }
};
```

### 3. Validation Logic

**Method**: `ValidateCriticalFieldsAsync()`

**Validations Performed**:

1. **NULL/Empty Check**: Field value must not be NULL or whitespace
2. **Label Existence Check**: Field value must exist in Monday column metadata labels

**Called Before**:
- `CreateMondayItemAsync()` - Before creating new items
- `UpdateMondayItemAsync()` - Before updating existing items (only when `requiresDataUpdate=true`)

### 4. Enhanced Monday Metadata Provider

**Files**: 
- `Monday\IMondayMetadataProvider.cs`
- `Monday\MondayMetadataProvider.cs`

**Added Method**: `GetAllowedStatusLabelsAsync()`

Fetches allowed labels for status columns from Monday API (uses same underlying structure as dropdowns).

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

**Example**: `DocumentType` = "כתב תביעה" (exists in Monday labels)

**Result**:
- ✅ Validation passes
- ✅ Monday item created/updated normally
- ✅ DEBUG log shows validation success

**Log Output**:
```
[DEBUG] Critical field validated OK: TikCounter=900000, Field=DocumentType, Value='כתב תביעה'
```

## Critical Columns Configuration

### Current Critical Columns

| Field Name | Monday Column ID | Column Type | Validation Message |
|------------|------------------|-------------|-------------------|
| `DocumentType` | `color_mkxhq546` | status | Prevents automatic creation of wrong document types |
| `PlaintiffSide` | `color_mkxh8gsq` | status | Prevents incorrect party designation (צד תובע) |
| `DefendantSide` | `color_mkxh5x31` | status | Prevents incorrect party designation (צד נתבע) |

### Adding New Critical Columns

To add a new critical column, update `CriticalColumns` list in `SyncService.cs`:

```csharp
private static readonly List<CriticalColumnDefinition> CriticalColumns = new()
{
    // ... existing columns ...
    new CriticalColumnDefinition
    {
        FieldName = "NewFieldName",
        ColumnType = "status", // or "dropdown"
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
✅ **Complete Context**: Logs include all relevant info for debugging  
✅ **Graceful**: Other cases continue processing even if one fails  
✅ **No Silent Bugs**: No hidden defaults that mask data quality issues  
✅ **Extensible**: Easy to add new critical columns  
✅ **Production Safe**: Cached metadata, error handling, no blocking behavior  

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
