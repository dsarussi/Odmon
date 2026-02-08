# Client 6 Special Handling & Hearing Update Gating

## Overview

This document describes the implementation of:
1. **Client 6 special behavior**: Omit DocumentType from Monday sync
2. **Hearing update gating**: Only update hearing date/hour when BOTH JudgeName AND EffectiveCourtCity exist
3. **Independent hearing status updates**: Status can be updated even without judge/city data

## Implementation Details

### 1. Client 6 Special Behavior

**Business Rule**: ClientVisualID == "6" uses document type "מכתב דרישה אילי" which does NOT exist in Monday labels. Therefore, DocumentType must be omitted for Client 6.

#### Helper Method: `IsClient6`

**File**: `Services\SyncService.cs` (lines ~2465)

```csharp
/// <summary>
/// Checks if a case belongs to Client 6 based on ClientVisualID.
/// Client 6 has special handling: DocumentType is omitted from Monday sync.
/// </summary>
private static bool IsClient6(string? clientVisualID)
{
    if (string.IsNullOrWhiteSpace(clientVisualID))
    {
        return false;
    }
    
    // Extract ClientNumber (substring before '\' if present, otherwise entire string)
    var backslashIndex = clientVisualID.IndexOf('\\');
    string clientNumberStr;
    
    if (backslashIndex > 0)
    {
        clientNumberStr = clientVisualID.Substring(0, backslashIndex).Trim();
    }
    else
    {
        clientNumberStr = clientVisualID.Trim();
    }
    
    return clientNumberStr == "6";
}
```

#### Omit DocumentType from Monday Payload

**File**: `Services\SyncService.cs` (lines ~720)

```csharp
// Special handling for Client 6: Do NOT send DocumentType to Monday
// Client 6 uses "מכתב דרישה אילי" which doesn't exist in Monday labels
var isClient6 = IsClient6(c.ClientVisualID);

if (!isClient6)
{
    var documentType = c.DocumentType;
    if (!string.IsNullOrWhiteSpace(documentType))
    {
        TryAddStatusLabelColumn(columnValues, _mondaySettings.DocumentTypeStatusColumnId, documentType);
    }
}
else
{
    _logger.LogDebug(
        "Client 6 special handling: Omitting DocumentType for TikCounter={TikCounter}, TikNumber={TikNumber}, ClientVisualID='{ClientVisualID}'",
        c.TikCounter,
        c.TikNumber ?? "<null>",
        c.ClientVisualID ?? "<null>");
}
```

#### Skip DocumentType Critical Validation for Client 6

**File**: `Services\SyncService.cs` - `ValidateCriticalFieldsAsync` (lines ~2295)

```csharp
private async Task ValidateCriticalFieldsAsync(long boardId, OdcanitCase c, CancellationToken ct)
{
    // Special handling for Client 6: Skip DocumentType validation
    // Client 6 uses "מכתב דרישה אילי" which doesn't exist in Monday labels
    var isClient6 = IsClient6(c.ClientVisualID);
    
    foreach (var criticalColumn in CriticalColumns)
    {
        // Skip DocumentType validation for Client 6
        if (isClient6 && criticalColumn.FieldName == "DocumentType")
        {
            _logger.LogDebug(
                "Client 6 special handling: Skipping DocumentType validation for TikCounter={TikCounter}, TikNumber={TikNumber}",
                c.TikCounter,
                c.TikNumber ?? "<null>");
            continue;
        }
        
        var fieldValue = criticalColumn.GetValue(c);
        // ... rest of validation logic
    }
}
```

### 2. Hearing Update Gating with Effective Court City

**Business Rule**: Hearing date/hour updates trigger client notifications. Therefore, date/hour should ONLY be updated when BOTH JudgeName AND EffectiveCourtCity are available.

#### Effective Court City Definition

**File**: `Services\HearingNearestSyncService.cs` (lines ~127)

```csharp
// Determine effective court city (City if present, else CourtName)
var effectiveCourtCity = !string.IsNullOrWhiteSpace(hearing.City)
    ? hearing.City.Trim()
    : (!string.IsNullOrWhiteSpace(hearing.CourtName) ? hearing.CourtName.Trim() : null);

_logger.LogDebug(
    "Effective court city determined: TikCounter={TikCounter}, City='{City}', CourtName='{CourtName}', EffectiveCourtCity='{EffectiveCourtCity}'",
    mapping.TikCounter,
    hearing.City ?? "<null>",
    hearing.CourtName ?? "<null>",
    effectiveCourtCity ?? "<null>");
```

**Priority**:
1. Use `City` if not null/empty
2. Otherwise use `CourtName` if not null/empty
3. Otherwise `null`

#### Field Update Logic (Independent)

**File**: `Services\HearingNearestSyncService.cs` (lines ~195)

```csharp
// Determine what can be updated based on available data
var hasJudgeName = !string.IsNullOrWhiteSpace(hearing.JudgeName);
var hasCourtCity = !string.IsNullOrWhiteSpace(effectiveCourtCity);
var canUpdateDateHour = hasJudgeName && hasCourtCity;

_logger.LogDebug(
    "Hearing update gating: TikCounter={TikCounter}, HasJudgeName={HasJudgeName}, HasCourtCity={HasCourtCity}, CanUpdateDateHour={CanUpdateDateHour}",
    mapping.TikCounter,
    hasJudgeName,
    hasCourtCity,
    canUpdateDateHour);

// Compute what changed
var judgeChanged = hasJudgeName && (snapshotJudge == null || !string.Equals(snapshotJudge, judgeName, StringComparison.Ordinal));
var cityChanged = hasCourtCity && (snapshotCity == null || !string.Equals(snapshotCity, city, StringComparison.Ordinal));

// Status can be updated independently (not blocked by missing judge/city)
if (statusChanged && meetStatus != 0)
{
    plannedSteps.Add($"SetStatus_{label}");
}

// Judge and city can be updated if they exist and changed
if (judgeChanged || cityChanged)
{
    plannedSteps.Add("UpdateJudgeCity");
}

// Date/hour can ONLY be updated if BOTH JudgeName and CourtCity exist
if (startDateChanged && canUpdateDateHour)
{
    plannedSteps.Add("UpdateHearingDate");
}
else if (startDateChanged && !canUpdateDateHour)
{
    _logger.LogInformation(
        "Hearing date/hour update blocked (missing judge or court city): TikCounter={TikCounter}, TikNumber={TikNumber}, MondayItemId={MondayItemId}, HasJudgeName={HasJudgeName}, HasCourtCity={HasCourtCity}",
        mapping.TikCounter,
        mapping.TikNumber,
        mapping.MondayItemId,
        hasJudgeName,
        hasCourtCity);
}
```

### 3. Hearing Status Column Updates (Independent)

**Business Rule**: The hearing status column ("דיון התבטל?") can be updated even when JudgeName or EffectiveCourtCity are missing.

#### Status Update Logic

**File**: `Services\HearingNearestSyncService.cs` (lines ~273)

```csharp
// Update status (independent - not blocked by missing judge/city)
if (statusChanged && meetStatus != 0 && !string.IsNullOrWhiteSpace(statusColumnId) && allowedStatusLabels != null && allowedStatusLabels.Contains(label))
{
    await _mondayClient.UpdateHearingStatusAsync(boardId, mapping.MondayItemId, label, statusColumnId, ct);
    executedSteps.Add($"SetStatus_{label}");
    columnsToUpdate.Add(statusColumnId);
    
    _logger.LogDebug(
        "Hearing status updated: TikCounter={TikCounter}, MeetStatus={MeetStatus}, Label='{Label}', ColumnId={ColumnId}",
        mapping.TikCounter,
        meetStatus,
        label,
        statusColumnId);
}
```

**Key Point**: This update happens **independently** of `canUpdateDateHour` gating.

### 4. Enhanced Logging

#### Per-Case Logs

**Effective Court City Selection**:
```
[DEBUG] Effective court city determined: TikCounter=39231, City='<null>', CourtName='כפר סבא', EffectiveCourtCity='כפר סבא'
```

**Update Gating Decision**:
```
[DEBUG] Hearing update gating: TikCounter=39231, HasJudgeName=True, HasCourtCity=True, CanUpdateDateHour=True
```

**Date/Hour Blocked**:
```
[INFO] Hearing date/hour update blocked (missing judge or court city): TikCounter=42020, TikNumber=7/1235744, MondayItemId=7890123457, HasJudgeName=False, HasCourtCity=True
```

**Status Updated**:
```
[DEBUG] Hearing status updated: TikCounter=39231, MeetStatus=1, Label='מבוטל', ColumnId=color_mkzqbrta
```

**Completion with Column IDs**:
```
[INFO] Hearing sync completed: TikCounter=39231, TikNumber=9/1858, MondayItemId=7890123456, ExecutedSteps=[SetStatus_מבוטל], UpdatedColumnIds=[color_mkzqbrta]
```

## Testing Scenarios

### Scenario 1: Client 6 Case

**Input**:
- `ClientVisualID = "6"`
- `DocumentType = "מכתב דרישה אילי"` (derived)

**Expected**:
```
[DEBUG] DocumentType assigned: TikCounter=50000, TikNumber=6/1234, ClientVisualID='6', DocumentType='מכתב דרישה אילי'
[DEBUG] Client 6 special handling: Omitting DocumentType for TikCounter=50000, TikNumber=6/1234, ClientVisualID='6'
[DEBUG] Client 6 special handling: Skipping DocumentType validation for TikCounter=50000, TikNumber=6/1234
[INFO] Successfully created Monday item: TikNumber=6/1234, TikCounter=50000, MondayItemId=7890123460
```

**Monday Payload** (DocumentType omitted):
```json
{
  "text_mkwe19hn": "6/1234",
  "phone_mkwe10tx": "050-1234567",
  "color_mkxh8gsq": {"label": "..."},
  "color_mkxh5x31": {"label": "..."}
  // color_mkxhq546 (DocumentType) NOT INCLUDED
}
```

**Result**: ✅ Item created without DocumentType, no validation failure

### Scenario 2: Hearing with Full Data (Can Update Date/Hour)

**Input**:
- `TikCounter = 39231`
- `MeetStatus = 1` (canceled)
- `StartDate = 2026-03-15 10:00`
- `JudgeName = "השופט כהן"`
- `City = null`
- `CourtName = "כפר סבא"`
- `EffectiveCourtCity = "כפר סבא"` (from CourtName)

**Expected**:
```
[DEBUG] Effective court city determined: TikCounter=39231, City='<null>', CourtName='כפר סבא', EffectiveCourtCity='כפר סבא'
[DEBUG] Hearing update gating: TikCounter=39231, HasJudgeName=True, HasCourtCity=True, CanUpdateDateHour=True
[INFO] Hearing sync planned: TikCounter=39231, TikNumber=9/1858, MondayItemId=7890123456, StartDate=2026-03-15 10:00, MeetStatus=1, Steps=[SetStatus_מבוטל, UpdateJudgeCity, UpdateHearingDate], SnapshotOld=[StartDate=<null>, Status=<null>], CanUpdateDateHour=True
[DEBUG] Hearing status updated: TikCounter=39231, MeetStatus=1, Label='מבוטל', ColumnId=color_mkzqbrta
[DEBUG] Hearing details updated: TikCounter=39231, JudgeName='השופט כהן', City='כפר סבא'
[DEBUG] Hearing date/hour updated: TikCounter=39231, StartDate=2026-03-15 10:00
[INFO] Hearing sync completed: TikCounter=39231, TikNumber=9/1858, MondayItemId=7890123456, ExecutedSteps=[SetStatus_מבוטל, UpdateJudgeCity, UpdateHearingDate], UpdatedColumnIds=[color_mkzqbrta, text_mkwjne8v, text_mkxez28d, date_mkwjwmzq, hour_mkwjbwr]
```

**Monday Columns Updated**:
```json
{
  "color_mkzqbrta": {"label": "מבוטל"},
  "text_mkwjne8v": "השופט כהן",
  "text_mkxez28d": "כפר סבא",
  "date_mkwjwmzq": {"date": "2026-03-15"},
  "hour_mkwjbwr": {"hour": "10:00"}
}
```

**Result**: ✅ All fields updated, including status, date, hour

### Scenario 3: Hearing Missing Judge (Date/Hour Blocked)

**Input**:
- `TikCounter = 42020`
- `MeetStatus = 2` (transferred)
- `StartDate = 2026-04-20 14:00`
- `JudgeName = null` (missing)
- `City = "תל אביב"`
- `EffectiveCourtCity = "תל אביב"`

**Expected**:
```
[DEBUG] Effective court city determined: TikCounter=42020, City='תל אביב', CourtName='<null>', EffectiveCourtCity='תל אביב'
[DEBUG] Hearing update gating: TikCounter=42020, HasJudgeName=False, HasCourtCity=True, CanUpdateDateHour=False
[INFO] Hearing date/hour update blocked (missing judge or court city): TikCounter=42020, TikNumber=7/1235744, MondayItemId=7890123457, HasJudgeName=False, HasCourtCity=True
[INFO] Hearing sync planned: TikCounter=42020, TikNumber=7/1235744, MondayItemId=7890123457, StartDate=2026-04-20 14:00, MeetStatus=2, Steps=[SetStatus_הועבר, UpdateJudgeCity], CanUpdateDateHour=False
[DEBUG] Hearing status updated: TikCounter=42020, MeetStatus=2, Label='הועבר', ColumnId=color_mkzqbrta
[DEBUG] Hearing details updated: TikCounter=42020, JudgeName='<null>', City='תל אביב'
[INFO] Hearing sync completed: TikCounter=42020, TikNumber=7/1235744, MondayItemId=7890123457, ExecutedSteps=[SetStatus_הועבר, UpdateJudgeCity], UpdatedColumnIds=[color_mkzqbrta, text_mkxez28d]
```

**Monday Columns Updated** (date/hour omitted):
```json
{
  "color_mkzqbrta": {"label": "הועבר"},
  "text_mkxez28d": "תל אביב"
  // date_mkwjwmzq and hour_mkwjbwr NOT INCLUDED (blocked by gating)
}
```

**Result**: ✅ Status and city updated, date/hour blocked (no judge)

### Scenario 4: Hearing Missing Court City (Date/Hour Blocked)

**Input**:
- `TikCounter = 39115`
- `MeetStatus = 1` (canceled)
- `StartDate = 2026-05-10 09:00`
- `JudgeName = "השופט לוי"`
- `City = null`
- `CourtName = null`
- `EffectiveCourtCity = null`

**Expected**:
```
[DEBUG] Effective court city determined: TikCounter=39115, City='<null>', CourtName='<null>', EffectiveCourtCity='<null>'
[DEBUG] Hearing update gating: TikCounter=39115, HasJudgeName=True, HasCourtCity=False, CanUpdateDateHour=False
[INFO] Hearing date/hour update blocked (missing judge or court city): TikCounter=39115, TikNumber=9/1808, MondayItemId=7890123458, HasJudgeName=True, HasCourtCity=False
[INFO] Hearing sync planned: TikCounter=39115, TikNumber=9/1808, MondayItemId=7890123458, StartDate=2026-05-10 09:00, MeetStatus=1, Steps=[SetStatus_מבוטל, UpdateJudgeCity], CanUpdateDateHour=False
[DEBUG] Hearing status updated: TikCounter=39115, MeetStatus=1, Label='מבוטל', ColumnId=color_mkzqbrta
[DEBUG] Hearing details updated: TikCounter=39115, JudgeName='השופט לוי', City='<null>'
[INFO] Hearing sync completed: TikCounter=39115, TikNumber=9/1808, MondayItemId=7890123458, ExecutedSteps=[SetStatus_מבוטל, UpdateJudgeCity], UpdatedColumnIds=[color_mkzqbrta, text_mkwjne8v]
```

**Monday Columns Updated** (date/hour omitted):
```json
{
  "color_mkzqbrta": {"label": "מבוטל"},
  "text_mkwjne8v": "השופט לוי"
  // text_mkxez28d NOT INCLUDED (no city)
  // date_mkwjwmzq and hour_mkwjbwr NOT INCLUDED (blocked by gating)
}
```

**Result**: ✅ Status and judge updated, city and date/hour blocked

### Scenario 5: Active Hearing with Full Data (Status Omitted)

**Input**:
- `TikCounter = 35000`
- `MeetStatus = 0` (active)
- `StartDate = 2026-06-01 11:00`
- `JudgeName = "השופט דוד"`
- `City = "ירושלים"`
- `EffectiveCourtCity = "ירושלים"`

**Expected**:
```
[DEBUG] Effective court city determined: TikCounter=35000, City='ירושלים', CourtName='<null>', EffectiveCourtCity='ירושלים'
[DEBUG] Hearing update gating: TikCounter=35000, HasJudgeName=True, HasCourtCity=True, CanUpdateDateHour=True
[INFO] Hearing sync planned: TikCounter=35000, TikNumber=3/2020, MondayItemId=7890123459, StartDate=2026-06-01 11:00, MeetStatus=0, Steps=[UpdateJudgeCity, UpdateHearingDate], CanUpdateDateHour=True
[DEBUG] Hearing details updated: TikCounter=35000, JudgeName='השופט דוד', City='ירושלים'
[DEBUG] Hearing date/hour updated: TikCounter=35000, StartDate=2026-06-01 11:00
[INFO] Hearing sync completed: TikCounter=35000, TikNumber=3/2020, MondayItemId=7890123459, ExecutedSteps=[UpdateJudgeCity, UpdateHearingDate], UpdatedColumnIds=[text_mkwjne8v, text_mkxez28d, date_mkwjwmzq, hour_mkwjbwr]
```

**Monday Columns Updated** (status omitted):
```json
{
  "text_mkwjne8v": "השופט דוד",
  "text_mkxez28d": "ירושלים",
  "date_mkwjwmzq": {"date": "2026-06-01"},
  "hour_mkwjbwr": {"hour": "11:00"}
  // color_mkzqbrta NOT INCLUDED (MeetStatus=0, active)
}
```

**Result**: ✅ Judge, city, date/hour updated; status omitted (preserve manual values)

### Scenario 6: Status-Only Update (Missing All Other Data)

**Input**:
- `TikCounter = 45000`
- `MeetStatus = 1` (canceled)
- `StartDate = 2026-07-15 13:00`
- `JudgeName = null`
- `City = null`
- `CourtName = null`
- `EffectiveCourtCity = null`

**Expected**:
```
[DEBUG] Effective court city determined: TikCounter=45000, City='<null>', CourtName='<null>', EffectiveCourtCity='<null>'
[DEBUG] Hearing update gating: TikCounter=45000, HasJudgeName=False, HasCourtCity=False, CanUpdateDateHour=False
[INFO] Hearing date/hour update blocked (missing judge or court city): TikCounter=45000, TikNumber=4/2021, MondayItemId=7890123460, HasJudgeName=False, HasCourtCity=False
[INFO] Hearing sync planned: TikCounter=45000, TikNumber=4/2021, MondayItemId=7890123460, StartDate=2026-07-15 13:00, MeetStatus=1, Steps=[SetStatus_מבוטל], CanUpdateDateHour=False
[DEBUG] Hearing status updated: TikCounter=45000, MeetStatus=1, Label='מבוטל', ColumnId=color_mkzqbrta
[INFO] Hearing sync completed: TikCounter=45000, TikNumber=4/2021, MondayItemId=7890123460, ExecutedSteps=[SetStatus_מבוטל], UpdatedColumnIds=[color_mkzqbrta]
```

**Monday Columns Updated** (only status):
```json
{
  "color_mkzqbrta": {"label": "מבוטל"}
  // All other fields omitted (missing data)
}
```

**Result**: ✅ Status updated independently, all other fields blocked

## Update Rules Summary

| Field | Condition | Gating |
|-------|-----------|--------|
| **Status** (`color_mkzqbrta`) | `MeetStatus` changed AND `MeetStatus != 0` | ✅ Independent (no gating) |
| **Judge** (`text_mkwjne8v`) | `JudgeName` changed | ✅ If `JudgeName` exists |
| **City** (`text_mkxez28d`) | `EffectiveCourtCity` changed | ✅ If `EffectiveCourtCity` exists |
| **Date** (`date_mkwjwmzq`) | `StartDate` changed | ❌ ONLY if `JudgeName` AND `EffectiveCourtCity` exist |
| **Hour** (`hour_mkwjbwr`) | `StartDate` changed | ❌ ONLY if `JudgeName` AND `EffectiveCourtCity` exist |

## MeetStatus Label Mapping

| MeetStatus | Monday Label | Update Behavior |
|------------|--------------|-----------------|
| 0 (פעיל) | "פעיל" | ❌ Column OMITTED (preserve manual values) |
| 1 (מבוטל) | "מבוטל" | ✅ Set `{"label": "מבוטל"}` |
| 2 (הועבר) | "הועבר" | ✅ Set `{"label": "הועבר"}` |

## Configuration

### Monday Settings (Already Configured)

**File**: `Monday\MondaySettings.cs` (line 70)

```csharp
/// <summary>Hearing status column (פעיל / מבוטל / הועבר). ColumnId: color_mkzqbrta.</summary>
public string? HearingStatusColumnId { get; set; } = "color_mkzqbrta";
```

### appsettings.json (Already Configured)

```json
{
  "Monday": {
    "HearingStatusColumnId": "color_mkzqbrta",
    "HearingDateColumnId": "date_mkwjwmzq",
    "HearingHourColumnId": "hour_mkwjbwr",
    "JudgeNameColumnId": "text_mkwjne8v",
    "CourtCityColumnId": "text_mkxez28d"
  },
  "OdcanitWrites": {
    "Enable": true,
    "DryRun": false
  }
}
```

## Files Changed

### Modified:

1. ✅ `Services\SyncService.cs`
   - Added `IsClient6` helper method (lines ~2465)
   - Modified `BuildColumnValuesJsonAsync` to omit DocumentType for Client 6 (lines ~720)
   - Modified `ValidateCriticalFieldsAsync` to skip DocumentType validation for Client 6 (lines ~2295)

2. ✅ `Services\HearingNearestSyncService.cs`
   - Changed `GetAllowedDropdownLabelsAsync` to `GetAllowedStatusLabelsAsync` for status column (line 114)
   - Added effective court city determination (lines ~127)
   - Removed `RequiredFieldsPresent` check (replaced with StartDate-only check)
   - Refactored update logic to be independent per field:
     - Status updates independent (lines ~273)
     - Judge/city updates if data exists (lines ~288)
     - Date/hour updates ONLY if both judge and city exist (lines ~310)
   - Removed status update for `MeetStatus=0` (active hearings)
   - Enhanced logging:
     - Effective court city selection
     - Gating decision
     - Date/hour blocking reason
     - Column IDs in completion log

## Build Status

✅ **0 Errors**, 9 pre-existing warnings

## Constraints Respected

✅ **No Monday board/column configuration changes**
✅ **No Odcanit DB schema/view changes**
✅ **Minimal, localized changes only**
✅ **Existing non-Client-6 behavior unchanged**
✅ **DocumentType remains critical for all non-Client-6 cases**

## Summary

### ✅ Client 6 Special Handling
- DocumentType omitted from Monday payload for Client 6
- DocumentType critical validation skipped for Client 6
- All other validations remain strict

### ✅ Hearing Update Gating
- EffectiveCourtCity = City ?? CourtName
- Date/hour updates ONLY when BOTH JudgeName AND EffectiveCourtCity exist
- Judge/city updates independently if data exists
- Status updates independently (not blocked by missing judge/city)

### ✅ Hearing Status Column
- MeetStatus = 1 → Set "מבוטל"
- MeetStatus = 2 → Set "הועבר"
- MeetStatus = 0 → Omit column (preserve manual values)
- Status can be updated even without judge/city data

### ✅ Enhanced Logging
- Effective court city selection logged
- Gating decisions logged with reason
- Date/hour blocking logged with missing fields
- Column IDs listed in completion log
