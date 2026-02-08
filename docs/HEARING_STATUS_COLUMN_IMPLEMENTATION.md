# Hearing Status Column (דיון התבטל?) - Implementation Summary

## Overview

Support for the Monday status column "דיון התבטל?" (Has hearing been canceled?) is **ALREADY IMPLEMENTED** in `HearingNearestSyncService`. This document describes the implementation and the fixes applied.

## Column Details

- **Column ID**: `color_mkzqbrta`
- **Column Title**: "דיון התבטל?" (Has hearing been canceled?)
- **Column Type**: Status (color)
- **Relevant Labels**: "מבוטל" (canceled), "הועבר" (transferred), "פעיל" (active)

## Configuration

### MondaySettings.cs

The column ID is already defined (line 70):

```csharp
/// <summary>Hearing status column (פעיל / מבוטל / הועבר). ColumnId: color_mkzqbrta.</summary>
public string? HearingStatusColumnId { get; set; } = "color_mkzqbrta";
```

### appsettings.json

The configuration is already present in the Monday section:

```json
{
  "Monday": {
    "HearingStatusColumnId": "color_mkzqbrta"
  }
}
```

## Data Source

- **View**: `vwExportToOuterSystems_YomanData` (diary/hearing events)
- **Model**: `OdcanitDiaryEvent`
- **Properties**:
  - `MeetStatus` (int?): 0=active, 1=canceled, 2=transferred
  - `MeetStatusName` (string?): "פעיל", "מבוטל", "העברה"

## Business Rules Implementation

### Rule 1: MeetStatus = 1 (Canceled) → "מבוטל"

**File**: `Services\HearingNearestSyncService.cs` (lines 247-254)

```csharp
else if (meetStatus == 1)
{
    if (statusChanged && !string.IsNullOrWhiteSpace(statusColumnId) && allowedStatusLabels != null && allowedStatusLabels.Contains(label))
    {
        await _mondayClient.UpdateHearingStatusAsync(boardId, mapping.MondayItemId, label, statusColumnId, ct);
        executedSteps.Add("SetStatus_מבוטל");
    }
}
```

### Rule 2: MeetStatus = 2 (Transferred) → "הועבר"

**File**: `Services\HearingNearestSyncService.cs` (lines 211-227)

```csharp
if (meetStatus == 2)
{
    if (statusChanged && !string.IsNullOrWhiteSpace(statusColumnId) && allowedStatusLabels != null && allowedStatusLabels.Contains(label))
    {
        await _mondayClient.UpdateHearingStatusAsync(boardId, mapping.MondayItemId, label, statusColumnId, ct);
        executedSteps.Add("SetStatus_הועבר");
    }
    if (judgeOrCityChanged)
    {
        await _mondayClient.UpdateHearingDetailsAsync(boardId, mapping.MondayItemId, judgeName, city, judgeCol, cityCol, ct);
        executedSteps.Add("UpdateJudgeCity");
    }
    if (startDateChanged)
    {
        await _mondayClient.UpdateHearingDateAsync(boardId, mapping.MondayItemId, hearing.StartDate!.Value, dateCol, hourCol, ct);
        executedSteps.Add("UpdateHearingDate");
    }
}
```

### Rule 3: MeetStatus = 0 (Active) → DO NOT SET

**File**: `Services\HearingNearestSyncService.cs` (lines 229-244)

```csharp
else if (meetStatus == 0)
{
    // When MeetStatus is active (0), do NOT touch the hearing status column
    // to avoid overriding manual values and reverting from canceled/transferred back to active
    if (judgeOrCityChanged)
    {
        await _mondayClient.UpdateHearingDetailsAsync(boardId, mapping.MondayItemId, judgeName, city, judgeCol, cityCol, ct);
        executedSteps.Add("UpdateJudgeCity");
    }
    if (startDateChanged)
    {
        await _mondayClient.UpdateHearingDateAsync(boardId, mapping.MondayItemId, hearing.StartDate!.Value, dateCol, hourCol, ct);
        executedSteps.Add("UpdateHearingDate");
    }
    // Omit status update when active (MeetStatus=0)
}
```

**Key Point**: When `MeetStatus=0`, the code updates judge/city and hearing date, but **does NOT** update the status column. This prevents overriding manual values and reverting from canceled/transferred back to active.

## Label Mapping

**File**: `Services\HearingNearestSyncService.cs` (lines 27-32)

```csharp
/// <summary>MeetStatus: 0=active, 1=cancelled, 2=rescheduled.</summary>
private static readonly IReadOnlyDictionary<int, string> MeetStatusToLabel = new Dictionary<int, string>
{
    [0] = "פעיל",
    [1] = "מבוטל",
    [2] = "הועבר"
};
```

## Fixes Applied

### Fix 1: Use Correct Metadata API (Line 114)

**Before**:
```csharp
allowedStatusLabels = await _mondayMetadataProvider.GetAllowedDropdownLabelsAsync(boardId, statusColumnId, ct);
```

**After**:
```csharp
// HearingStatusColumnId is a status column (type=color), not dropdown
allowedStatusLabels = await _mondayMetadataProvider.GetAllowedStatusLabelsAsync(boardId, statusColumnId, ct);
```

**Reason**: `HearingStatusColumnId` is a status column (`type="color"`), not a dropdown column. Using `GetAllowedDropdownLabelsAsync` would fail to retrieve the correct labels.

### Fix 2: Do NOT Set Status When MeetStatus = 0 (Lines 229-244)

**Before**:
```csharp
else if (meetStatus == 0)
{
    // ... update judge/city and date ...
    
    if (statusChanged && !string.IsNullOrWhiteSpace(statusColumnId) && allowedStatusLabels != null && allowedStatusLabels.Contains(label))
    {
        await _mondayClient.UpdateHearingStatusAsync(boardId, mapping.MondayItemId, label, statusColumnId, ct);
        executedSteps.Add("SetStatus_פעיל");
    }
}
```

**After**:
```csharp
else if (meetStatus == 0)
{
    // When MeetStatus is active (0), do NOT touch the hearing status column
    // to avoid overriding manual values and reverting from canceled/transferred back to active
    
    // ... update judge/city and date ...
    
    // Omit status update when active (MeetStatus=0)
}
```

**Reason**: When a hearing is active (`MeetStatus=0`), we should NOT touch the Monday status column to:
1. Avoid overriding manual values set by users
2. Prevent reverting from "מבוטל" or "הועבר" back to "פעיל"

## Null Safety

The implementation includes null safety checks:

1. **MeetStatus has value**: `var meetStatus = hearing.MeetStatus ?? 0;` (line 137)
2. **Label validation**: Only updates if label exists in `allowedStatusLabels` (lines 144-165)
3. **Column ID exists**: Only updates if `statusColumnId` is not null/empty

## Logging

### Debug Logging (Line 184)

```csharp
_logger.LogDebug(
    "Hearing sync no-op (no change): TikCounter={TikCounter}, MondayItemId={MondayItemId}, StartDate={StartDate}, MeetStatus={MeetStatus}",
    mapping.TikCounter, mapping.MondayItemId, hearing.StartDate!.Value.ToString("yyyy-MM-dd HH:mm"), meetStatus);
```

### Information Logging (Line 189)

```csharp
_logger.LogInformation(
    "Hearing sync planned: TikCounter={TikCounter}, TikNumber={TikNumber}, MondayItemId={MondayItemId}, StartDate={StartDate}, MeetStatus={MeetStatus}, Steps=[{Steps}], SnapshotOld=[StartDate={SnapshotStart}, Status={SnapshotStatus}]",
    mapping.TikCounter, mapping.TikNumber, mapping.MondayItemId,
    hearing.StartDate!.Value.ToString("yyyy-MM-dd HH:mm"), meetStatus, string.Join(", ", plannedSteps),
    snapshotStartUtc?.ToString("yyyy-MM-dd HH:mm") ?? "<null>", snapshotStatus?.ToString() ?? "<null>");
```

### Execution Logging

When status is actually updated, the executed step is logged:
- `"SetStatus_מבוטל"` for canceled hearings
- `"SetStatus_הועבר"` for transferred hearings
- No status step for active hearings (intentionally omitted)

## Monday API Calls

### UpdateHearingStatusAsync

**Method Signature**:
```csharp
Task UpdateHearingStatusAsync(long boardId, long itemId, string statusLabel, string columnId, CancellationToken ct)
```

**Called for**:
- `MeetStatus = 1` → `statusLabel = "מבוטל"`
- `MeetStatus = 2` → `statusLabel = "הועבר"`
- `MeetStatus = 0` → **NOT CALLED**

### Expected JSON Payloads

#### Scenario 1: Hearing Canceled (MeetStatus = 1)

```json
{
  "color_mkzqbrta": {
    "label": "מבוטל"
  }
}
```

#### Scenario 2: Hearing Transferred (MeetStatus = 2)

```json
{
  "color_mkzqbrta": {
    "label": "הועבר"
  }
}
```

#### Scenario 3: Hearing Active (MeetStatus = 0)

**Column is omitted from the update** (not included in `column_values` JSON).

## Testing Scenarios

### Test 1: Canceled Hearing

**Data**:
- `TikCounter = 39231`
- `MeetStatus = 1`
- `MeetStatusName = "מבוטל"`

**Expected**:
```
[INFO] Hearing sync planned: TikCounter=39231, TikNumber=9/1858, MondayItemId=7890123456, StartDate=2026-03-15 10:00, MeetStatus=1, Steps=[SetStatus_מבוטל], ...
[INFO] Hearing sync completed: TikCounter=39231, MondayItemId=7890123456, ExecutedSteps=[SetStatus_מבוטל]
```

**Monday Column**:
```json
{
  "color_mkzqbrta": {
    "label": "מבוטל"
  }
}
```

### Test 2: Transferred Hearing

**Data**:
- `TikCounter = 42020`
- `MeetStatus = 2`
- `MeetStatusName = "העברה"`
- `StartDate` changed

**Expected**:
```
[INFO] Hearing sync planned: TikCounter=42020, TikNumber=7/1235744, MondayItemId=7890123457, StartDate=2026-04-20 14:00, MeetStatus=2, Steps=[SetStatus_הועבר, UpdateJudgeCity, UpdateHearingDate], ...
[INFO] Hearing sync completed: TikCounter=42020, MondayItemId=7890123457, ExecutedSteps=[SetStatus_הועבר, UpdateJudgeCity, UpdateHearingDate]
```

**Monday Columns**:
```json
{
  "color_mkzqbrta": {
    "label": "הועבר"
  },
  "date_mkwjwmzq": {
    "date": "2026-04-20"
  },
  "hour_mkwjbwr": {
    "hour": "14:00"
  },
  "text_mkwjne8v": "השופט כהן",
  "text_mkxez28d": "תל אביב"
}
```

### Test 3: Active Hearing (Status Column Omitted)

**Data**:
- `TikCounter = 39115`
- `MeetStatus = 0`
- `MeetStatusName = "פעיל"`
- `JudgeName` changed

**Expected**:
```
[INFO] Hearing sync planned: TikCounter=39115, TikNumber=9/1808, MondayItemId=7890123458, StartDate=2026-05-10 09:00, MeetStatus=0, Steps=[UpdateJudgeCity], ...
[INFO] Hearing sync completed: TikCounter=39115, MondayItemId=7890123458, ExecutedSteps=[UpdateJudgeCity]
```

**Monday Columns** (status column NOT included):
```json
{
  "text_mkwjne8v": "השופט לוי",
  "text_mkxez28d": "ירושלים"
}
```

**Note**: `color_mkzqbrta` is **NOT** included in the update to preserve any manual value.

### Test 4: Invalid Label (Skip with Warning)

**Data**:
- `TikCounter = 50000`
- `MeetStatus = 1`
- Label "מבוטל" not in Monday allowed labels

**Expected**:
```
[WARN] Hearing status label 'מבוטל' not found on Monday column color_mkzqbrta for board 5035534500; skipping status update for TikCounter=50000, MondayItemId=7890123459.
[INFO] SkipEvent logged: TikCounter=50000, Operation=MondayColumnValueValidation, ReasonCode=monday_invalid_status_label
```

**Result**: Status column is NOT updated (validation failure).

## Configuration Requirements

### Minimal Required Configuration

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

### Enable Hearing Sync

Hearing sync is controlled by `OdcanitWrites:Enable` config:
- `Enable = false` → Hearing sync disabled entirely
- `Enable = true, DryRun = true` → Logs what would be done, no actual updates
- `Enable = true, DryRun = false` → Live updates to Monday

## Build Status

✅ **0 Errors**, 9 pre-existing warnings

## Summary

### ✅ Implementation Status

- ✅ Column ID configured in MondaySettings
- ✅ Label mapping implemented
- ✅ Business rules correctly applied:
  - MeetStatus = 1 → Set "מבוטל"
  - MeetStatus = 2 → Set "הועבר"
  - MeetStatus = 0 → **Do NOT set** (omit column)
- ✅ Null safety checks in place
- ✅ Logging for monitoring and debugging
- ✅ Label validation against Monday metadata
- ✅ Uses correct API (`GetAllowedStatusLabelsAsync` for status columns)

### ✅ Fixes Applied

1. **Fixed metadata API call**: Changed from `GetAllowedDropdownLabelsAsync` to `GetAllowedStatusLabelsAsync`
2. **Fixed active hearing logic**: Removed status update when `MeetStatus=0` to avoid overriding manual values

The hearing status column ("דיון התבטל?") is now fully functional and follows all business rules correctly.
