# ODMON Monday.com Field Mapping

This document lists all field mappings from Odcanit to Monday.com, including data sources, column IDs, and special logic.

## TikNumber-Based Update Behavior

- **Primary Key**: TikNumber (e.g., "6/2524") is used as the logical identifier for cases in Monday.com
- **Lookup Strategy**:
  1. First checks IntegrationDbContext mapping table by TikNumber + BoardId (preferred) or TikCounter (fallback)
  2. If no mapping found and TikNumber exists, queries Monday.com API by TikNumber in "מספר תיק" column (text_mkwe19hn)
  3. If existing item found: Creates mapping record and updates the item
  4. If no existing item: Creates new item and mapping record
- **Update Logic**: When a mapping exists, compares OdcanitVersion (tsModifyDate) to detect changes and updates all mapped columns via change_multiple_column_values mutation
- **One-to-One Rule**: There is at most ONE Monday.com item per TikNumber on each board

## Field Mappings

### Case Identification

| Monday Column | Column ID | Odcanit Source | Notes |
|--------------|-----------|----------------|-------|
| מספר תיק | text_mkwe19hn | `OdcanitCase.TikNumber` | Used as primary key for lookup/update |
| מספר לקוח | dropdown_mkxjrssr | `OdcanitCase.ClientVisualID` | Dropdown column |
| מספר תביעה | text_mkwjy5pg | `OdcanitCase.Additional ?? OdcanitCase.HozlapTikNumber` | Fallback to HozlapTikNumber if Additional is empty |

### Dates

| Monday Column | Column ID | Odcanit Source | Notes |
|--------------|-----------|----------------|-------|
| תאריך פתיחת תיק | date4 | `OdcanitCase.tsCreateDate` | Case creation date |
| תאריך אירוע | date_mkwj3780 | `OdcanitCase.EventDate` | From "Dor screen" user data (FieldName: "תאריך אירוע" or "Event date") |
| תאריך סגירת תיק | date_mkweqkjf | `OdcanitCase.TikCloseDate` | Case close date |
| מועד קבלת כתב התביעה | date_mkxeapah | `OdcanitCase.ComplaintReceivedDate` | From "Dor screen" user data (FieldName: "מועד קבלת כתב התביעה") or `HozlapOpenDate` |
| תאריך דיון | date_mkwjwmzq | `OdcanitCase.HearingDate` | From diary events (vwDiaryEvents) - first event with court info |
| שעת דיון | hour_mkwjbwr | `OdcanitCase.HearingTime` | From diary events (vwDiaryEvents) - TimeOfDay from FromTime or ToTime |

### Financial Amounts

| Monday Column | Column ID | Odcanit Source | Notes |
|--------------|-----------|----------------|-------|
| סכום תביעה | numeric_mkxw7s29 | `OdcanitCase.RequestedClaimAmount` | From "Dor screen" user data (FieldName: "סכום תביעה", "הסעד המבוקש ( סכום תביעה)", "Claim amount") |
| סכום תביעה מוכח | numeric_mkwjcrwk | `OdcanitCase.ProvenClaimAmount` | From "Dor screen" user data (FieldName: "סכום תביעה מוכח") |
| סכום פסק דין | numeric_mkwj6mnw | `OdcanitCase.JudgmentAmount` | From "Dor screen" user data (FieldName: "סכום פסק דין") |

### Client Information

| Monday Column | Column ID | Odcanit Source | Notes |
|--------------|-----------|----------------|-------|
| טלפון | phone_mkwe10tx | `OdcanitCase.PolicyHolderPhone` (normalized) | Phone normalized to E.164 format (+972...). Precedence: PolicyHolderPhone > DriverPhone > ClientPhone |
| דוא"ל | email_mkwefwgy | `OdcanitCase.ClientEmail` | From vwExportToOuterSystems_UserData (Mobile/Email) |
| כתובת לקוח | text_mkwjcc69 | `OdcanitCase.ClientAddress` | From vwExportToOuterSystems_UserData (FullAddress) |
| ח.פ. לקוח | text_mkwjzsvg | `OdcanitCase.ClientTaxId` | From "Dor screen" user data (FieldName: "ח.פ. לקוח") |

### Policy Holder Information

| Monday Column | Column ID | Odcanit Source | Notes |
|--------------|-----------|----------------|-------|
| שם בעל פוליסה | *Dynamic* | `OdcanitCase.PolicyHolderName` | Column ID resolved dynamically by title "שם בעל פוליסה" via MondayMetadataProvider |
| תעודת זהות בעל פוליסה | text_mkwjqdb4 | `OdcanitCase.PolicyHolderId` | From "Dor screen" user data (FieldName: "ת.ז. בעל פוליסה", "תעודת זהות בעל פוליסה", "Policy holder: id") |
| כתובת בעל פוליסה | text_mkwjan1q | `OdcanitCase.PolicyHolderAddress` | From "Dor screen" user data (FieldName: "כתובת בעל פוליסה", "Policy holder: address") |
| טלפון בעל פוליסה | phone_mkwjzg9 | `OdcanitCase.PolicyHolderPhone` (normalized) | From "Dor screen" user data (FieldName: "סלולרי בעל פוליסה", "Policy holder: phone"). Normalized to E.164 format |
| דוא"ל בעל פוליסה | email_mkwjbh2t | `OdcanitCase.PolicyHolderEmail` | From "Dor screen" user data (FieldName: "כתובת דוא\"ל בעל פוליסה", "Policy holder: email") |

### Driver Information

| Monday Column | Column ID | Odcanit Source | Notes |
|--------------|-----------|----------------|-------|
| שם נהג | text_mkwja7cv | `OdcanitCase.DriverName` | From "Dor screen" user data (FieldName: "שם נהג", "Driver: name") |
| תעודת זהות נהג | text_mkwjbtre | `OdcanitCase.DriverId` | From "Dor screen" user data (FieldName: "תעודת זהות נהג", "Driver: id") |
| טלפון נהג | phone_mkwj7fak | `OdcanitCase.DriverPhone` (normalized) | From "Dor screen" user data (FieldName: "סלולרי נהג", "סלולרי עד", "Driver: phone"). Normalized to E.164 format |

### Vehicle Information

| Monday Column | Column ID | Odcanit Source | Notes |
|--------------|-----------|----------------|-------|
| מספר רכב ראשי | text_mkwjnwh7 | `OdcanitCase.MainCarNumber` | From "Dor screen" user data (FieldName: "מספר רישוי", "Main car number", "Driver: main car number") |
| מספר רכב צד ג' | text_mkwj5jpn | `OdcanitCase.ThirdPartyCarNumber` | From "Dor screen" user data (FieldName: "מספר רישוי רכב ג'", "מספר רכב צד ג'", "Third-party driver: car number") |

### Plaintiff Information (from vwSides)

| Monday Column | Column ID | Odcanit Source | Notes |
|--------------|-----------|----------------|-------|
| שם תובע | text_mkwj5k8e | `OdcanitCase.PlaintiffName` | From vwSides where SideTypeName indicates Plaintiff role |
| תעודת זהות תובע | text_mkwj82zd | `OdcanitCase.PlaintiffId` | From vwSides (ID field) |
| כתובת תובע | text_mkwjvvp6 | `OdcanitCase.PlaintiffAddress` | From vwSides (FullAddress field) |
| טלפון תובע | phone_mkwjm44s | `OdcanitCase.PlaintiffPhone` (normalized) | From "Dor screen" user data (FieldName: "סלולרי תובע"). Normalized to E.164 format |
| דוא"ל תובע | email_mkwjy4rs | `OdcanitCase.PlaintiffEmail` | From "Dor screen" user data (FieldName: "כתובת דוא\"ל תובע") |

### Defendant Information (from vwSides)

| Monday Column | Column ID | Odcanit Source | Notes |
|--------------|-----------|----------------|-------|
| שם נתבע | text_mkxeabj2 | `OdcanitCase.DefendantName` | From vwSides where SideTypeName indicates Defendant role |
| פקס | text_mkxe2zay | `OdcanitCase.DefendantFax` | From "Dor screen" user data (FieldName: "פקס") |

### Third Party Information

| Monday Column | Column ID | Odcanit Source | Notes |
|--------------|-----------|----------------|-------|
| שם נהג צד ג' | text_mkwj9bvj | `OdcanitCase.ThirdPartyDriverName` | From vwSides (SideTypeName = ThirdParty) or "Dor screen" user data (FieldName: "שם נהג צד ג'", "Third-party driver: name") |
| תעודת זהות נהג צד ג' | text_mkwjmad2 | `OdcanitCase.ThirdPartyDriverId` | From vwSides (ID field) or "Dor screen" user data (FieldName: "ת.ז. נהג צד ג'", "Third-party driver: id") |
| טלפון צד ג' | phone_mkwj9a3a | `OdcanitCase.ThirdPartyPhone` (normalized) | From "Dor screen" user data (FieldName: "נייד צד ג'", "Third-party driver: phone"). Normalized to E.164 format |
| שם מעסיק צד ג' | text_mkwj6b | `OdcanitCase.ThirdPartyEmployerName` | From "Dor screen" user data (FieldName: "שם מעסיק צד ג'") |
| מספר זהות מעסיק צד ג' | text_mkwjfkbm | `OdcanitCase.ThirdPartyEmployerId` | From "Dor screen" user data (FieldName: "מספר זהות מעסיק צד ג'") |
| כתובת מעסיק צד ג' | text_mkwjgpd2 | `OdcanitCase.ThirdPartyEmployerAddress` | From "Dor screen" user data (FieldName: "כתובת מעסיק צד ג'") |
| מיוצג על ידי עו"ד צד ג' | text_mkwj1w08 | `OdcanitCase.ThirdPartyLawyerName` | From "Dor screen" user data (FieldName: "מיוצג על ידי עו\"ד צד ג'") |
| כתובת עו"ד צד ג' | text_mkwjdzdg | `OdcanitCase.ThirdPartyLawyerAddress` | From "Dor screen" user data (FieldName: "כתובת עו\"ד צד ג'") |
| טלפון עו"ד צד ג' | phone_mkwjfge2 | `OdcanitCase.ThirdPartyLawyerPhone` (normalized) | From "Dor screen" user data (FieldName: "טלפון עו\"ד צד ג'"). Normalized to E.164 format |
| דוא"ל עו"ד צד ג' | email_mkwj4mmk | `OdcanitCase.ThirdPartyLawyerEmail` | From "Dor screen" user data (FieldName: "כתובת דוא\"ל עו\"ד צד ג'") |

### Insurance Company Information

| Monday Column | Column ID | Odcanit Source | Notes |
|--------------|-----------|----------------|-------|
| חברה מבטחת צד ג' | color_mkwjz9mp | `OdcanitCase.ThirdPartyInsurerName` | Status column. From "Dor screen" user data (FieldName: "חברה מבטחת צד ג'", "Third-party driver: insurer name") |
| ח.פ. חברת ביטוח | text_mkwjmpex | `OdcanitCase.InsuranceCompanyId` | From "Dor screen" user data (FieldName: "ח.פ. חברת ביטוח") |
| כתובת חברת ביטוח | text_mkwjnvdr | `OdcanitCase.InsuranceCompanyAddress` | From "Dor screen" user data (FieldName: "כתובת חברת ביטוח") |
| דוא"ל חברת ביטוח | email_mkwjv6zw | `OdcanitCase.InsuranceCompanyEmail` | From "Dor screen" user data (FieldName: "כתובת דוא\"ל חברת ביטוח") |

### Court Information

| Monday Column | Column ID | Odcanit Source | Notes |
|--------------|-----------|----------------|-------|
| שם בית משפט | color_mkwj24j | `OdcanitCase.CourtName` | Status column. From diary events (vwDiaryEvents) - CourtName or CourtCodeName |
| עיר בית משפט | text_mkxez28d | `OdcanitCase.CourtCity` | From diary events (vwDiaryEvents) - City field |
| מספר תיק בבית משפט | text_mkwj3kf4 | `OdcanitCase.CourtCaseNumber` | From vwHozlapFormsData_TikMainData - concatenation of clcCourtNum + CourtName |
| שם שופט | text_mkwjne8v | `OdcanitCase.JudgeName` | From diary events (vwDiaryEvents) - JudgeName field |

### Legal & Administrative

| Monday Column | Column ID | Odcanit Source | Notes |
|--------------|-----------|----------------|-------|
| שם עורך דין | text_mkxeqj54 | `OdcanitCase.AttorneyName` | From "Dor screen" user data (FieldName: "שם עורך דין") |
| מרחוב (הגנה) | text_mkxer5d1 | `OdcanitCase.DefenseStreet` | From "Dor screen" user data (FieldName: "מרחוב (הגנה)") |
| מרחוב (תביעה) | text_mkxwzxcq | `OdcanitCase.ClaimStreet` | From "Dor screen" user data (FieldName: "מרחוב (תביעה)") |
| folderID | text_mkxe3vhk | `OdcanitCase.CaseFolderId` | From "Dor screen" user data (FieldName: "folderID") |
| הערות | long_text_mkwe5h8v | `OdcanitCase.Notes` | Case notes |
| נתבעים נוספים | long_text_mkwjhngq | `OdcanitCase.AdditionalDefendants` | From "Dor screen" user data (FieldName: "נתבעים נוספים") |
| שם עד | text_mkwjt62y | `OdcanitCase.WitnessName` | From "Dor screen" user data (FieldName: "שם עד") |

### Status & Classification Fields

| Monday Column | Column ID | Odcanit Source | Notes |
|--------------|-----------|----------------|-------|
| סטטוס תיק | color_mkwefnbx | `OdcanitCase.StatusName` | Status column. Mapped via MapStatusIndex(): "סגור"/"closed" → index 1, "פתוח"/"open"/"עבודה" → index 0, "תקוע"/"stuck" → index 2, default → index 5. On new items, set to "חדש" (new) |
| סוג מסמך | color_mkxhq546 | **Computed from ClientVisualID** | **Document Type Logic**: Client number = 1 → "כתב הגנה" (defense), Client number in {4, 7, 9} OR ≥ 100 → "כתב תביעה" (claim), Otherwise → empty |
| סוג משימה | color_mkwyq310 | `OdcanitCase.TikType` | Status column. Mapped via MapTaskTypeLabel(): Contains "פגיש" → "פגישה", Contains "מכתב"/"דריש" → "מכתב דרישה", Contains "זימון"/"דיון" → "זימון לדיון", Contains "הודע" → "הודעה", Default → "טיפול בתיק" |
| אחראי | text_mkxz6j9y | **Computed from OdcanitCase** | **Responsible Logic**: Referant → TeamName → TikOwner (as string). From DetermineResponsibleText() |

## Data Source Summary

### Odcanit Database Views/Tables

- **vwExportToOuterSystems_Files**: Base case data (TikNumber, TikCounter, tsCreateDate, etc.)
- **vwExportToOuterSystems_UserData**: Client contact info (phone, email, address)
- **vwSides**: Case sides (plaintiff, defendant, third party) with names, IDs, addresses
- **vwDiaryEvents**: Court hearings, dates, judge names, court information
- **vwExportToOuterSystems_UserData** (Dor screen): Policy holder, driver, vehicle, financial, and legal data via FieldName matching
- **vwHozlapFormsData_TikMainData**: Court case numbers (clcCourtNum, CourtName)

### Enrichment Process

1. **Client Enrichment**: Loads client contact info from vwExportToOuterSystems_UserData
2. **Sides Enrichment**: Loads plaintiff, defendant, third party from vwSides
3. **Diary Events Enrichment**: Loads court hearings and events from vwDiaryEvents
4. **User Data Enrichment**: Loads "Dor screen" data (PageName = "פרטי תיק נזיקין מליגל") and maps by FieldName
5. **Hozlap Main Data Enrichment**: Loads court case numbers from vwHozlapFormsData_TikMainData

## Special Processing

### Phone Number Normalization

- All phone numbers are normalized to E.164 international format (+972...) for Israeli numbers
- Non-digits are stripped, and +972 is prepended for local numbers
- Applied to: PolicyHolderPhone, DriverPhone, PlaintiffPhone, ThirdPartyPhone, ThirdPartyLawyerPhone

### Item Name Construction

- Format: `{ClientName} - שם בעל פוליסה: {PolicyHolderName} ({TikNumber})` (if both exist)
- Fallback: `{ClientName} ({TikNumber})` or `{PolicyHolderName} ({TikNumber})` or `{TikName} ({TikNumber})` or just `{TikNumber}`
- In test mode: Prepends "[TEST] " prefix

### Dynamic Column Resolution

- Policy holder name column ID is resolved dynamically by title "שם בעל פוליסה" using MondayMetadataProvider
- Cached per board to avoid repeated API calls

