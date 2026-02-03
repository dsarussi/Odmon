# OdmonTestCase DTO Implementation

## Problem Solved

When running TEST MODE with `Testing.Source = IntegrationDbOdmonTestCases`, the table `dbo.OdmonTestCases` contains Hebrew column names **with underscores** (e.g., `נזק_ישיר`, `הפסדים`, `מספר_תיק_בית_משפט`), but the sync logic was trying to map them to `OdcanitCase` properties using Hebrew names **with spaces** (e.g., `נזק ישיר`, `הפסדים`, `מספר הליך בית משפט`).

This mismatch caused all values from `dbo.OdmonTestCases` to be NULL, resulting in empty Monday items.

## Solution

Created a dedicated `OdmonTestCase` DTO with Hebrew property names **with underscores** matching the exact table column names, then convert to `OdcanitCase` before syncing.

## Implementation

### 1. New Model: `OdmonTestCase`

**File**: `Models\OdmonTestCase.cs`

Properties match Hebrew column names exactly (with underscores):

```csharp
public class OdmonTestCase
{
    public int Id { get; set; }
    public int TikCounter { get; set; }
    public string מספר_תיק { get; set; }
    public string? נזק_ישיר { get; set; }
    public string? הפסדים { get; set; }
    public string? מספר_תיק_בית_משפט { get; set; }
    public string? שם_שופט { get; set; }
    public DateTime? תאריך_דיון { get; set; }
    public TimeSpan? שעה { get; set; }
    public decimal? הסעד_המבוקש_סכום_תביעה { get; set; }
    public string? טלפון_בעל_פוליסה { get; set; }
    public string? נסיבות_התאונה_בקצרה { get; set; }
    // ... 70+ properties total
    
    public OdcanitCase ToOdcanitCase() { /* conversion logic */ }
}
```

**Key Features**:
- Properties match **exact** Hebrew column names with underscores
- Includes all financial fields (נזק_ישיר, הפסדים, ירידת_ערך, שווי_שרידים, etc.)
- Includes all court fields (מספר_תיק_בית_משפט, שם_שופט, תאריך_דיון, שעה)
- Includes all contact fields (טלפון_בעל_פוליסה, סלולרי_תובע, etc.)
- `ToOdcanitCase()` method converts to `OdcanitCase` for existing sync logic

### 2. Updated IntegrationTestCaseSource

**File**: `Services\IntegrationTestCaseSource.cs`

**Logic**:
1. Detect source type from `Testing:Source` config
2. If source contains "OdmonTestCases" → load Hebrew underscore columns
3. Otherwise → load Hebrew space columns (legacy behavior)

**Key Changes**:

```csharp
public async Task<List<OdcanitCase>> GetCasesByTikCountersAsync(...)
{
    var source = testingSection.GetValue<string>("Source") ?? "IntegrationDbOdmonTestCases";
    var isOdmonTestCases = source.Contains("OdmonTestCases", StringComparison.OrdinalIgnoreCase);
    
    if (isOdmonTestCases)
    {
        return await LoadOdmonTestCasesAsync(tableName, list, ct);
    }
    
    // Legacy path: Hebrew names with spaces
    // ...
}

private async Task<List<OdcanitCase>> LoadOdmonTestCasesAsync(...)
{
    // Load rows into OdmonTestCase DTO
    var testCase = new OdmonTestCase
    {
        מספר_תיק = GetString(raw, "מספר_תיק"),
        נזק_ישיר = GetDecimal(raw, "נזק_ישיר"),
        הפסדים = GetDecimal(raw, "הפסדים"),
        מספר_תיק_בית_משפט = GetString(raw, "מספר_תיק_בית_משפט"),
        שם_שופט = GetString(raw, "שם_שופט"),
        תאריך_דיון = GetDate(raw, "תאריך_דיון"),
        שעה = GetTimeSpan(raw, "שעה"),
        הסעד_המבוקש_סכום_תביעה = GetDecimal(raw, "הסעד_המבוקש_סכום_תביעה"),
        // ... all fields
    };
    
    // Convert to OdcanitCase for compatibility
    var odcanitCase = testCase.ToOdcanitCase();
    cases.Add(odcanitCase);
}
```

## Configuration

**appsettings.json**:

```json
{
  "Testing": {
    "Enable": true,
    "Source": "IntegrationDbOdmonTestCases",  // Triggers OdmonTestCase loading
    "TikCounters": [900000],
    "TableName": "dbo.OdmonTestCases"
  }
}
```

## Column Mapping Reference

| Table Column (with _) | OdmonTestCase Property | OdcanitCase Property | Monday Column |
|------------------------|------------------------|----------------------|---------------|
| `מספר_תיק` | `מספר_תיק` | `TikNumber` | `text_mkwe19hn` |
| `נזק_ישיר` | `נזק_ישיר` | `DirectDamageAmount` | `numeric_mky1jccw` |
| `הפסדים` | `הפסדים` | `OtherLossesAmount` | `numeric_mky1tv4r` |
| `ירידת_ערך` | `ירידת_ערך` | `LossOfValueAmount` | `numeric_mky23vbb` |
| `שווי_שרידים` | `שווי_שרידים` | `ResidualValueAmount` | `numeric_mkzjw4z7` |
| `שכ_ט_שמאי` | `שכ_ט_שמאי` | `AppraiserFeeAmount` | `numeric_mky2n7hz` |
| `מספר_תיק_בית_משפט` | `מספר_תיק_בית_משפט` | `CourtCaseNumber` | `text_mkwj3kf4` |
| `שם_שופט` | `שם_שופט` | `JudgeName` | `text_mkwjne8v` |
| `תאריך_דיון` | `תאריך_דיון` | `HearingDate` | `date_mkwjwmzq` |
| `שעה` | `שעה` | `HearingTime` | `hour_mkwjbwr` |
| `הסעד_המבוקש_סכום_תביעה` | `הסעד_המבוקש_סכום_תביעה` | `RequestedClaimAmount` | `numeric_mkxw7s29` |
| `טלפון_בעל_פוליסה` | `טלפון_בעל_פוליסה` | `PolicyHolderPhone` | `phone_mkwe10tx` |
| `נסיבות_התאונה_בקצרה` | `נסיבות_התאונה_בקצרה` | `Notes` | `long_text_mkwe5h8v` |
| `צד_תובע` | `צד_תובע` | `PlaintiffSideRaw` | `color_mkxh8gsq` |
| `צד_נתבע` | `צד_נתבע` | `DefendantSideRaw` | `color_mkxh5x31` |
| `סוג_מסמך` | `סוג_מסמך` | `DocumentType` | `color_mkxhq546` |

*(See `OdmonTestCase.cs` for complete list of 70+ column mappings)*

## Data Flow

```
dbo.OdmonTestCases (Hebrew underscore columns)
    ↓
IntegrationTestCaseSource.LoadOdmonTestCasesAsync()
    ↓
OdmonTestCase DTO (Hebrew underscore properties)
    ↓
OdmonTestCase.ToOdcanitCase()
    ↓
OdcanitCase (English properties)
    ↓
SyncService.BuildColumnValuesJsonAsync()
    ↓
Monday.com columns
```

## Logging

**Startup**:
```
[IntegrationTestCaseSource] Reading test cases from IntegrationDb table dbo.OdmonTestCases (Source=IntegrationDbOdmonTestCases) for TikCounters=900000
[IntegrationTestCaseSource] Loading OdmonTestCase entities (Hebrew columns with underscores) from dbo.OdmonTestCases
```

**Per-Case**:
```
[IntegrationTestCaseSource] Loaded OdmonTestCase: Id=1, TikCounter=900000, מספר_תיק=5-1-1808, נזק_ישיר=50000.00, הפסדים=10000.00
```

**Summary**:
```
[IntegrationTestCaseSource] Loaded 4 OdmonTestCase(s) from dbo.OdmonTestCases, converted to OdcanitCase for sync
```

## Testing

1. **Populate Test Data**:
   ```sql
   INSERT INTO dbo.OdmonTestCases (
       TikCounter, מספר_תיק, נזק_ישיר, הפסדים, 
       מספר_תיק_בית_משפט, שם_שופט, תאריך_דיון, שעה
   ) VALUES (
       900001, '5-1-1808', 50000, 10000,
       '12345-01-2024', 'שופט דוגמה', '2024-12-01', '10:00'
   )
   ```

2. **Configure**:
   ```json
   {
     "Testing": {
       "Enable": true,
       "Source": "IntegrationDbOdmonTestCases",
       "TikCounters": [900001],
       "TableName": "dbo.OdmonTestCases"
     }
   }
   ```

3. **Run**:
   ```bash
   dotnet run
   ```

4. **Verify Monday Item**:
   - נזק ישיר column should show 50,000
   - הפסדים column should show 10,000
   - מספר תיק בית משפט should show "12345-01-2024"
   - שם שופט should show "שופט דוגמה"

## Important Notes

- **No Database Schema Changes**: Only DTO/mapping changes
- **No Production Impact**: Only affects TEST MODE (`Testing:Enable=true`)
- **Backward Compatible**: Legacy test tables with spaces still work
- **Full Field Coverage**: All 70+ fields from table are mapped
- **Type Safety**: Proper DateTime, TimeSpan, decimal conversions

## Benefits

✅ Test cases now populate ALL Monday columns (notes, damages, court, phones, amounts)  
✅ Exact same behavior as real Odcanit cases  
✅ No manual mapping required - direct column-to-property match  
✅ Easy to add new fields - just add to DTO and conversion method  
✅ Full traceability with detailed logging  

## Troubleshooting

**Issue**: Monday items still empty

**Check**:
1. Verify `Testing:Source` = `IntegrationDbOdmonTestCases`
2. Check column names in table (must use underscores: `מספר_תיק` not `מספר תיק`)
3. Look for log: "Loading OdmonTestCase entities (Hebrew columns with underscores)"
4. Check per-case debug logs for actual values loaded

**Issue**: Some fields NULL

**Check**:
1. Verify column names match exactly (underscores, Hebrew characters)
2. Check for quote variations in column names (e.g., `דוא_ל` vs `דוא"ל`)
3. Review `LoadOdmonTestCasesAsync` for fallback mappings (e.g., `?? GetString(raw, "alt_name")`)
