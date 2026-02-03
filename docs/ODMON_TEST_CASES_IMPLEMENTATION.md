# OdmonTestCases Implementation Guide

## Overview

Added support for end-to-end testing using `dbo.OdmonTestCases` table in IntegrationDb. This allows running complete test flows (Monday sync + document generation) using test data instead of production Odcanit data.

## Implementation Summary

### 1. New Reader: `OdmonTestCasesReader`

**File**: `Services\OdmonTestCasesReader.cs`

**Purpose**: Reads test cases from `dbo.OdmonTestCases` table and maps to `OdcanitCase` domain objects.

**Key Features**:
- Filters out reserve rows (where `מספר_תיק` is NULL or whitespace)
- Supports optional filtering by `MaxId` and `OnlyIds`
- Generates synthetic `TikCounter` values (900000 + Id) for test cases
- Comprehensive logging of eligible rows and loaded cases
- Fail-fast validation if enabled but no eligible rows found
- Maps all Hebrew column names (with underscores) to domain properties

### 2. Configuration

**File**: `appsettings.json` and `appsettings.Development.json`

Added `OdmonTestCases` section:

```json
{
  "OdmonTestCases": {
    "Enable": false,
    "MaxId": null,
    "OnlyIds": null
  }
}
```

**Configuration Options**:

| Key | Type | Description | Example |
|-----|------|-------------|---------|
| `Enable` | bool | Feature flag to switch to OdmonTestCases reader | `true` / `false` |
| `MaxId` | int? | Optional: Load only rows with Id <= MaxId | `4` |
| `OnlyIds` | string? | Optional: Comma-separated list of specific Ids to load | `"1,3,5"` |

**Priority**:
1. If `OdmonTestCases:Enable=true` → Use `OdmonTestCasesReader`
2. Else if `Testing:Enable=true` → Use `IntegrationTestCaseSource` (legacy)
3. Else → Use `OdcanitCaseSource` (production)

### 3. Dependency Injection

**File**: `Program.cs` (lines 100-121)

Registered `OdmonTestCasesReader` and updated `ICaseSource` factory:

```csharp
services.AddScoped<OdmonTestCasesReader>();
services.AddScoped<ICaseSource>(sp =>
{
    var config = sp.GetRequiredService<IConfiguration>();
    
    // Priority 1: OdmonTestCases (end-to-end test/demo mode)
    var odmonTestCasesEnabled = config.GetValue<bool>("OdmonTestCases:Enable", false);
    if (odmonTestCasesEnabled)
    {
        return sp.GetRequiredService<OdmonTestCasesReader>();
    }
    
    // Priority 2: IntegrationTestCaseSource (legacy test mode)
    var testingEnabled = config.GetValue<bool>("Testing:Enable", false);
    if (testingEnabled)
    {
        return sp.GetRequiredService<IntegrationTestCaseSource>();
    }

    // Default: Production Odcanit reader
    return sp.GetRequiredService<OdcanitCaseSource>();
});
```

## Database Schema

### Table: `dbo.OdmonTestCases` (IntegrationDb)

**Required columns** (Hebrew with underscores):

- `Id` (int, primary key)
- `מספר_תיק` (nvarchar) - **Required, non-null for eligible rows**
- `סוג_מסמך` (nvarchar) - Document type
- `צד_תובע` (nvarchar) - Plaintiff side
- `צד_נתבע` (nvarchar) - Defendant side
- `שם_בעל_פוליסה` (nvarchar) - Policy holder name
- `שם_נהג` (nvarchar) - Driver name
- `תאריך_אירוע` (datetime) - Event date
- `תאריך_פתיחת_תיק` (datetime) - Case open date
- `מספר_תיק_בית_משפט` (nvarchar) - Court case number
- `שם_שופט` (nvarchar) - Judge name
- `שעה` (time) - Hearing time
- `הסעד_המבוקש_סכום_תביעה` (decimal) - Requested claim amount

**Full column mapping** (70+ fields supported):

See `OdmonTestCasesReader.MapRowToCase()` for complete list. Includes:
- Client information (name, phone, email, address, tax ID)
- Policy holder details
- Driver information
- Plaintiff and defendant details
- Third-party information
- Court and hearing details
- Financial amounts (claims, damages, fees)
- Dates and timestamps

## SQL Queries Used

### 1. Count Eligible Rows

```sql
SELECT COUNT(*) 
FROM dbo.OdmonTestCases 
WHERE מספר_תיק IS NOT NULL 
  AND LTRIM(RTRIM(מספר_תיק)) <> ''
```

### 2. Load Test Cases (Base Query)

```sql
SELECT * 
FROM dbo.OdmonTestCases 
WHERE מספר_תיק IS NOT NULL 
  AND LTRIM(RTRIM(מספר_תיק)) <> ''
ORDER BY Id
```

### 3. Load with MaxId Filter

```sql
SELECT * 
FROM dbo.OdmonTestCases 
WHERE מספר_תיק IS NOT NULL 
  AND LTRIM(RTRIM(מספר_תיק)) <> ''
  AND Id <= @MaxId
ORDER BY Id
```

### 4. Load with OnlyIds Filter

```sql
SELECT * 
FROM dbo.OdmonTestCases 
WHERE מספר_תיק IS NOT NULL 
  AND LTRIM(RTRIM(מספר_תיק)) <> ''
  AND Id IN (@Id0, @Id1, @Id2, ...)
ORDER BY Id
```

## Logging Output

### Startup Logs

```
[OdmonTestCasesReader] Loading test cases from dbo.OdmonTestCases. Filters: MaxId=4, OnlyIds=none
[OdmonTestCasesReader] dbo.OdmonTestCases: 12 eligible rows (מספר_תיק not null)
```

### Per-Case Logs

```
[OdmonTestCasesReader] Loaded test case: Id=1, מספר_תיק=5-1-1808, סוג_מסמך=כתב תביעה, צד_תובע=תובע, צד_נתבע=נתבע
[OdmonTestCasesReader] Loaded test case: Id=2, מספר_תיק=5-2-1808, סוג_מסמך=כתב הגנה, צד_תובע=נתבע, צד_נתבע=תובע
```

### Summary Log

```
[OdmonTestCasesReader] Loaded 4 test case(s) from dbo.OdmonTestCases after filters
```

### Error Cases

**No eligible rows**:
```
InvalidOperationException: OdmonTestCases:Enable=true but zero eligible rows found in dbo.OdmonTestCases 
(all rows have NULL מספר_תיק). Please add valid test data or disable OdmonTestCases:Enable.
```

**No rows after filtering**:
```
InvalidOperationException: OdmonTestCases:Enable=true but zero rows matched filters (MaxId=10, OnlyIds=99,100). 
Adjust filters or add matching test data.
```

**Skipped reserve row**:
```
[OdmonTestCasesReader] Skipping OdmonTestCases row Id=5 with NULL/empty מספר_תיק
```

## Usage Examples

### Example 1: Load All Eligible Rows

**appsettings.json**:
```json
{
  "OdmonTestCases": {
    "Enable": true,
    "MaxId": null,
    "OnlyIds": null
  }
}
```

**Result**: Loads all rows from `dbo.OdmonTestCases` where `מספר_תיק` is not null.

### Example 2: Load First 4 Test Cases

**appsettings.json**:
```json
{
  "OdmonTestCases": {
    "Enable": true,
    "MaxId": 4,
    "OnlyIds": null
  }
}
```

**Result**: Loads only rows with `Id <= 4`.

### Example 3: Load Specific Test Cases

**appsettings.json**:
```json
{
  "OdmonTestCases": {
    "Enable": true,
    "MaxId": null,
    "OnlyIds": "1,3,7"
  }
}
```

**Result**: Loads only rows with `Id IN (1, 3, 7)`.

### Example 4: Combine Filters

**appsettings.json**:
```json
{
  "OdmonTestCases": {
    "Enable": true,
    "MaxId": 10,
    "OnlyIds": "2,4,6,8"
  }
}
```

**Result**: Loads rows where `Id IN (2,4,6,8) AND Id <= 10` (i.e., 2,4,6,8).

## Safety Features

1. **NULL Case Number Filter**: Automatically skips reserve rows with NULL/empty `מספר_תיק`
2. **Fail-Fast Validation**: Throws exception if enabled but no eligible rows found
3. **Synthetic TikCounter**: Uses 900000+ range to avoid collision with real cases
4. **Comprehensive Logging**: Full visibility into which cases are loaded and why
5. **Production Isolation**: OdmonTestCases reader only used when explicitly enabled

## Testing Workflow

1. **Prepare Test Data**: Insert rows into `dbo.OdmonTestCases` with Hebrew column names
2. **Configure Filters**: Set `OdmonTestCases:Enable=true` and optional filters
3. **Run Application**: `dotnet run`
4. **Verify Logs**: Check startup logs for eligible row count and loaded cases
5. **Test Monday Sync**: Verify Monday items are created/updated correctly
6. **Test Document Generation**: Verify documents are generated with test data

## Column Name Mapping Reference

| Hebrew Column (with _) | OdcanitCase Property | Type |
|------------------------|---------------------|------|
| `מספר_תיק` | `TikNumber` | string |
| `שם_תיק` | `TikName` | string |
| `שם_לקוח` | `ClientName` | string |
| `מספר_לקוח` | `ClientVisualID` | string |
| `טלפון_לקוח` | `ClientPhone` | string |
| `דוא"ל_לקוח` | `ClientEmail` | string |
| `כתובת_לקוח` | `ClientAddress` | string |
| `סטטוס` | `StatusName` | string |
| `סוג_תיק` | `TikType` | string |
| `תאריך_פתיחת_תיק` | `tsCreateDate` | DateTime? |
| `תאריך_אירוע` | `EventDate` | DateTime? |
| `שם_בעל_פוליסה` | `PolicyHolderName` | string |
| `ת_ז_בעל_פוליסה` | `PolicyHolderId` | string |
| `סלולרי_בעל_פוליסה` | `PolicyHolderPhone` | string |
| `שם_נהג` | `DriverName` | string |
| `תעודת_זהות_נהג` | `DriverId` | string |
| `שם_תובע` | `PlaintiffName` | string |
| `סלולרי_תובע` | `PlaintiffPhone` | string |
| `שם_נתבע` | `DefendantName` | string |
| `צד_תובע` | `PlaintiffSideRaw` | string |
| `צד_נתבע` | `DefendantSideRaw` | string |
| `שם_בית_משפט` | `CourtName` | string |
| `מספר_תיק_בית_משפט` | `CourtCaseNumber` | string |
| `שם_שופט` | `JudgeName` | string |
| `תאריך_דיון` | `HearingDate` | DateTime? |
| `שעה` | `HearingTime` | TimeSpan? |
| `הסעד_המבוקש_סכום_תביעה` | `RequestedClaimAmount` | decimal? |
| `סכום_פסק_דין` | `JudgmentAmount` | decimal? |
| `נזק_ישיר` | `DirectDamageAmount` | decimal? |
| `סוג_מסמך` | `DocumentType` | string |

*(See `OdmonTestCasesReader.MapRowToCase()` for complete list of 70+ mappings)*

## Notes

- **TikCounter Generation**: Test cases use synthetic TikCounter = 900000 + Id
- **No Odcanit DB Access**: Reader only accesses IntegrationDb, never touches Odcanit
- **Monday Board**: No Monday configuration changes required - treats Monday as black box
- **Document Templates**: No template changes required - just provides data
- **Production Safety**: OdmonTestCases reader only activates when explicitly enabled

## Maintenance

To add support for new columns:
1. Add column to `dbo.OdmonTestCases` table
2. Update `MapRowToCase()` in `OdmonTestCasesReader.cs`
3. Rebuild and test

## Environment Variables (Alternative to appsettings.json)

```bash
OdmonTestCases__Enable=true
OdmonTestCases__MaxId=4
OdmonTestCases__OnlyIds="1,2,3"
```
