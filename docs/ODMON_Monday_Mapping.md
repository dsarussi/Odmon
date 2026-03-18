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
| זיהוי נוסף | text_mm1gvfd5 | `OdcanitCase.AdditionalIdentification` | From legal user data (UserData view vwExportToOuterSystems_UserData, FieldName: "זיהוי נוסף") |

### Dates

| Monday Column | Column ID | Odcanit Source | Notes |
|--------------|-----------|----------------|-------|
| תאריך פתיחת תיק | date4 | `OdcanitCase.tsCreateDate` | Case creation date |
| תאריך אירוע | date_mkwj3780 | `OdcanitCase.EventDate` | From legal user data (UserData view vwExportToOuterSystems_UserData, FieldName: "תאריך אירוע" or "Event date") |
| תאריך סגירת תיק | date_mkweqkjf | `OdcanitCase.TikCloseDate` | Case close date |
| תאריך קבלת התביעה אצל הלקוח | date_mkxeapah | `OdcanitCase.ComplaintReceivedDate` | From legal user data (FieldName: "תאריך קבלת התביעה אצל הלקוח"). Falls back to `HozlapOpenDate` if not set. |
| תאריך אחרון להגשת כתב הטענות | date_mm1gex5r | `OdcanitCase.PleadingDeadlineDate` | From legal user data (UserData view vwExportToOuterSystems_UserData, FieldName: "תאריך אחרון להגשת כתב הטענות") |
| תאריך דיון | date_mkwjwmzq | `OdcanitCase.HearingDate` | From diary events (vwDiaryEvents) - first event with court info |
| שעת דיון | hour_mkwjbwr | `OdcanitCase.HearingTime` | From diary events (vwDiaryEvents) - TimeOfDay from FromTime or ToTime |

### Financial Amounts

| Monday Column | Column ID | Odcanit Source | Notes |
|--------------|-----------|----------------|-------|
| סכום תביעה | numeric_mkxw7s29 | `OdcanitCase.RequestedClaimAmount` | From legal user data (FieldName: "סכום תביעה"). |
| סכום פסק דין | numeric_mkwj6mnw | `OdcanitCase.JudgmentAmount` | From legal user data (FieldName: "סכום פסק דין") |
| סכום לתשלום | numeric_mm1gj81h | `OdcanitCase.PaymentDueAmount` | From legal user data (FieldName: "סכום לתשלום") |
| שכ"ט שמאי | numeric_mky2n7hz | `OdcanitCase.AppraiserFeeAmount` | From legal user data (FieldName: "שכ\"ט שמאי") |
| ירידת ערך | numeric_mky23vbb | `OdcanitCase.LossOfValueAmount` | From legal user data (FieldName: "ירידת ערך") |
| הפסדים | numeric_mky1tv4r | `OdcanitCase.OtherLossesAmount` | From legal user data (FieldName: "הפסדים") |
| שווי שרידים | numeric_mkzjw4z7 | `OdcanitCase.ResidualValueAmount` | From legal user data (FieldName: "שווי שרידים") |
| סכום תביעה צד ג | numeric_mky2m5a1 | `OdcanitCase.ThirdPartyClaimAmount` | From legal user data (FieldName: "סכום תביעה צד ג") |
| דמי כינון | numeric_mky2yr0y | `OdcanitCase.ReconstructionFeeAmount` | From legal user data (FieldName: "דמי כינון") |
| השתתפות עצמית לנזק | numeric_mky2ywrf | `OdcanitCase.DeductibleDamageAmount` | From legal user data (FieldName: "השתתפות עצמית לנזק") |
| תגמולי ביטוח | numeric_mkza3p82 | `OdcanitCase.InsuranceBenefitsAmount` | From legal user data (FieldName: "תגמולי ביטוח") |

### Client Information

| Monday Column | Column ID | Odcanit Source | Notes |
|--------------|-----------|----------------|-------|
| טלפון | phone_mkwe10tx | `OdcanitCase.PolicyHolderPhone` (normalized) | Phone normalized to E.164 format (+972...). Precedence: PolicyHolderPhone > DriverPhone > ClientPhone |
| דוא"ל | email_mkwefwgy | `OdcanitCase.ClientEmail` | From vwExportToOuterSystems_UserData (Mobile/Email) |
| כתובת לקוח | text_mkwjcc69 | `OdcanitCase.ClientAddress` | From vwExportToOuterSystems_UserData (FullAddress) |
| ח.פ. לקוח | text_mkwjzsvg | `OdcanitCase.ClientTaxId` | From legal user data (UserData view vwExportToOuterSystems_UserData, FieldName: "ח.פ. לקוח") |

### Policy Holder Information

| Monday Column | Column ID | Odcanit Source | Notes |
|--------------|-----------|----------------|-------|
| שם בעל פוליסה | text_mky27a51 | `OdcanitCase.PolicyHolderName` | From legal user data (FieldName: "שם בעל פוליסה"). Written to text column only; does NOT affect item name. |
| תעודת זהות בעל פוליסה | text_mkwjqdb4 | `OdcanitCase.PolicyHolderId` | From legal user data (UserData view vwExportToOuterSystems_UserData, FieldName: "ת.ז. בעל פוליסה", "תעודת זהות בעל פוליסה", "Policy holder: id") |
| כתובת בעל פוליסה | text_mkxer5d1 | `OdcanitCase.PolicyHolderAddress` | From legal user data (UserData view vwExportToOuterSystems_UserData, FieldName: "כתובת בעל פוליסה", "Policy holder: address") |
| טלפון בעל פוליסה | phone_mkwjzg9 | `OdcanitCase.PolicyHolderPhone` (normalized) | From legal user data (UserData view vwExportToOuterSystems_UserData, FieldName: "סלולרי בעל פוליסה", "Policy holder: phone"). Normalized to E.164 format |
| דוא"ל בעל פוליסה | email_mkwjbh2t | `OdcanitCase.PolicyHolderEmail` | From legal user data (FieldName: "כתובת דוא\"ל בעל פוליסה", "כתובת מייל מבוטח", "Policy holder: email") |

### Driver Information

| Monday Column | Column ID | Odcanit Source | Notes |
|--------------|-----------|----------------|-------|
| שם נהג | text_mkwja7cv | `OdcanitCase.DriverName` | From legal user data (UserData view vwExportToOuterSystems_UserData, FieldName: "שם נהג", "Driver: name") |
| תעודת זהות נהג | text_mkwjbtre | `OdcanitCase.DriverId` | From legal user data (UserData view vwExportToOuterSystems_UserData, FieldName: "תעודת זהות נהג", "Driver: id") |
| טלפון נהג | phone_mkwj7fak | `OdcanitCase.DriverPhone` (normalized) | From legal user data (UserData view vwExportToOuterSystems_UserData, FieldName: "סלולרי נהג", "סלולרי עד", "Driver: phone"). Normalized to E.164 format |

### Vehicle Information

| Monday Column | Column ID | Odcanit Source | Notes |
|--------------|-----------|----------------|-------|
| מספר רכב ראשי | text_mkwjnwh7 | `OdcanitCase.MainCarNumber` | From legal user data (FieldName: "מספר רישוי" with fallback to "מספר רישוי." if empty). First non-empty trimmed value used. |
| מספר רכב צד ג' | text_mky2df4d | `OdcanitCase.ThirdPartyCarNumber` | From legal user data (UserData view vwExportToOuterSystems_UserData, FieldName: "מספר רישוי רכב ג'", "מספר רישוי רכב ג", "מספר רכב צד ג'", "Third-party driver: car number"). Previous column id was text_mkwj5jpn (no longer active). |

### Plaintiff Information (from vwSides)

| Monday Column | Column ID | Odcanit Source | Notes |
|--------------|-----------|----------------|-------|
| שם תובע | text_mkwj5k8e | `OdcanitCase.PlaintiffName` | From vwSides where SideTypeName indicates Plaintiff role |
| תעודת זהות תובע | text_mkwj82zd | `OdcanitCase.PlaintiffId` | From vwSides (ID field) |
| כתובת תובע | text_mm1b2eaz | `OdcanitCase.PlaintiffAddress` | From vwSides (FullAddress field) |
| טלפון תובע | phone_mkwjm44s | `OdcanitCase.PlaintiffPhone` (normalized) | From legal user data (UserData view vwExportToOuterSystems_UserData, FieldName: "סלולרי תובע"). Normalized to E.164 format |
| דוא"ל תובע | email_mkwjy4rs | `OdcanitCase.PlaintiffEmail` | From legal user data (UserData view vwExportToOuterSystems_UserData, FieldName: "כתובת דוא\"ל תובע") |

### Defendant Information (from vwSides)

| Monday Column | Column ID | Odcanit Source | Notes |
|--------------|-----------|----------------|-------|
| שם נתבע | text_mkxeabj2 | `OdcanitCase.DefendantName` | From vwSides where SideTypeName indicates Defendant role |
| פקס | text_mkxe2zay | `OdcanitCase.DefendantFax` | From legal user data (UserData view vwExportToOuterSystems_UserData, FieldName: "פקס") |

### Third Party Information

| Monday Column | Column ID | Odcanit Source | Notes |
|--------------|-----------|----------------|-------|
| שם נהג צד ג' | text_mkwj9bvj | `OdcanitCase.ThirdPartyDriverName` | From vwSides (SideTypeName = ThirdParty) or legal user data (UserData view vwExportToOuterSystems_UserData, FieldName: "שם נהג צד ג'", "Third-party driver: name") |
| תעודת זהות נהג צד ג' | text_mkwjmad2 | `OdcanitCase.ThirdPartyDriverId` | From vwSides (ID field) or legal user data (UserData view vwExportToOuterSystems_UserData, FieldName: "ת.ז. נהג צד ג'", "Third-party driver: id") |
| טלפון צד ג' | phone_mkwj9a3a | `OdcanitCase.ThirdPartyPhone` (normalized) | From legal user data (FieldName: "נייד צד ג'", "נייד צד ג", "Third-party driver: phone"). Normalized to E.164 format |
| שם מעסיק צד ג' | text_mkwj6b | `OdcanitCase.ThirdPartyEmployerName` | From legal user data (UserData view vwExportToOuterSystems_UserData, FieldName: "שם מעסיק צד ג'") |
| מספר זהות מעסיק צד ג' | text_mkwjfkbm | `OdcanitCase.ThirdPartyEmployerId` | From legal user data (UserData view vwExportToOuterSystems_UserData, FieldName: "מספר זהות מעסיק צד ג'") |
| כתובת מעסיק צד ג' | text_mkwjgpd2 | `OdcanitCase.ThirdPartyEmployerAddress` | From legal user data (UserData view vwExportToOuterSystems_UserData, FieldName: "כתובת מעסיק צד ג'") |
| עו"ד צד ג | text_mkwj1w08 | `OdcanitCase.ThirdPartyLawyerName` | From legal user data (FieldName: "עו\"ד צד ג" only). |
| כתובת עו"ד צד ג' | text_mkwjdzdg | `OdcanitCase.ThirdPartyLawyerAddress` | From legal user data (UserData view vwExportToOuterSystems_UserData, FieldName: "כתובת עו\"ד צד ג'") |
| טלפון עו"ד צד ג' | phone_mkwjfge2 | `OdcanitCase.ThirdPartyLawyerPhone` (normalized) | From legal user data (UserData view vwExportToOuterSystems_UserData, FieldName: "טלפון עו\"ד צד ג'"). Normalized to E.164 format |
| דוא"ל עו"ד צד ג' | email_mkwj4mmk | `OdcanitCase.ThirdPartyLawyerEmail` | From legal user data (UserData view vwExportToOuterSystems_UserData, FieldName: "כתובת דוא\"ל עו\"ד צד ג'") |

### Insurance Company Information

| Monday Column | Column ID | Odcanit Source | Notes |
|--------------|-----------|----------------|-------|
| חברה מבטחת צד ג' | color_mkwjz9mp | `OdcanitCase.ThirdPartyInsurerName` | Status column. From legal user data (UserData view vwExportToOuterSystems_UserData, FieldName: "חברה מבטחת צד ג'", "Third-party driver: insurer name") |
| ח.פ. חברת ביטוח | text_mkwjmpex | `OdcanitCase.InsuranceCompanyId` | From legal user data (UserData view vwExportToOuterSystems_UserData, FieldName: "ח.פ. חברת ביטוח") |
| כתובת חברת ביטוח | text_mkwjnvdr | `OdcanitCase.InsuranceCompanyAddress` | From legal user data (UserData view vwExportToOuterSystems_UserData, FieldName: "כתובת חברת ביטוח") |
| דוא"ל חברת ביטוח | email_mkwjv6zw | `OdcanitCase.InsuranceCompanyEmail` | From legal user data (UserData view vwExportToOuterSystems_UserData, FieldName: "כתובת דוא\"ל חברת ביטוח") |
| חברת ביטוח 2 | text_mm1gy0q4 | `OdcanitCase.InsuranceCompany2` | From legal user data (UserData view vwExportToOuterSystems_UserData, FieldName: "חברת ביטוח 2") |
| כתובת חברת ביטוח 2 | text_mm1gmta2 | `OdcanitCase.InsuranceCompany2Address` | From legal user data (UserData view vwExportToOuterSystems_UserData, FieldName: "כתובת חברת ביטוח 2") |

### Court Information

| Monday Column | Column ID | Odcanit Source | Notes |
|--------------|-----------|----------------|-------|
| שם בית משפט | ~~color_mkwj24j~~ (removed — column does not exist on board) | `OdcanitCase.CourtName` | Was status column; removed from sync payload. |
| שם בית משפט (text) | text_mkxez28d | `OdcanitCase.LegalCourtName` | From legal UserData (vwExportToOuterSystems_UserData, FieldName: "שם בית משפט"). **Not** from diary events or CourtCity. |
| מספר הליך בית משפט | text_mkwj3kf4 | `OdcanitCase.CourtCaseNumber` | From legal user data (FieldName: "מספר הליך בית משפט") or Hozlap data |
| שם שופט | text_mkwjne8v | `OdcanitCase.HearingJudgeName` | From hearing diary events only (vwDiaryEvents). Not populated from legal UserData. |

### Legal & Administrative

| Monday Column | Column ID | Odcanit Source | Notes |
|--------------|-----------|----------------|-------|
| שם עורך דין | text_mkxeqj54 | `OdcanitCase.AttorneyName` | From legal user data (UserData view vwExportToOuterSystems_UserData, FieldName: "שם עורך דין") |
| כתובת נתבע / מרחוב (הגנה) | text_mkxwzxcq | `OdcanitCase.DefenseStreet` | From legal user data (UserData view vwExportToOuterSystems_UserData, FieldName: "כתובת נתבע" or "מרחוב (הגנה)") |
| מרחוב (תביעה) | _(no live column)_ | `OdcanitCase.ClaimStreet` | From legal user data — no corresponding Monday column; not sent. |
| folderID | text_mkxe3vhk | `OdcanitCase.CaseFolderId` | From legal user data (UserData view vwExportToOuterSystems_UserData, FieldName: "folderID") |
| גרסאות תביעה | long_text_mm1gsvg0 | `OdcanitCase.ClaimVersions` | From legal user data (UserData view vwExportToOuterSystems_UserData, FieldName: "גרסאות תביעה"). Also populates `OdcanitCase.Notes` → long_text_mkwe5h8v |
| הערות | long_text_mkwe5h8v | `OdcanitCase.Notes` | From UserData "גרסאות תביעה" |
| גרסאות הגנה | long_text_mm1gxq01 | `OdcanitCase.DefenseVersions` | From legal user data (UserData view vwExportToOuterSystems_UserData, FieldName: "גרסאות הגנה") |
| נסיבות התאונה בקצרה | text_mky1vzgg | `OdcanitCase.ShortAccidentCircumstances` | From UserData "גרסת לקוח - נוסח משפטי" |
| סוג הליך | text_mm1gsp8k | `OdcanitCase.ProceedingType` | From legal user data (UserData view vwExportToOuterSystems_UserData, FieldName: "סוג הליך") |
| נתבעים נוספים | long_text_mkwjhngq | `OdcanitCase.AdditionalDefendants` | From legal user data (UserData view vwExportToOuterSystems_UserData, FieldName: "נתבעים נוספים") |
| שם עד | text_mkwjt62y | `OdcanitCase.WitnessName` | From legal user data (UserData view vwExportToOuterSystems_UserData, FieldName: "שם עד") |

### Status & Classification Fields

| Monday Column | Column ID | Odcanit Source | Notes |
|--------------|-----------|----------------|-------|
| סטטוס תיק | color_mkwefnbx | `OdcanitCase.StatusName` | Status column. Mapped via MapStatusIndex(): "סגור"/"closed" → index 1, "פתוח"/"open"/"עבודה" → index 0, "תקוע"/"stuck" → index 2, default → index 5. On new items, set to "חדש" (new) |
| צד תובע | color_mkxh8gsq | `OdcanitCase.PlaintiffSideRaw` | Status column. From legal user data (FieldName: "צד תובע"). Mapped via MapPlaintiffSideLabel(). |
| צד נתבע | color_mkxh5x31 | `OdcanitCase.DefendantSideRaw` | Status column. From legal user data (FieldName: "צד נתבע"). Mapped via MapDefendantSideLabel(). |
| סוג מסמך | color_mkxhq546 | **Computed from ClientVisualID** | **Document Type Logic**: Client number = 1 → "כתב הגנה" (defense), Client number in {4, 7, 9} OR ≥ 100 → "כתב תביעה" (claim), Otherwise → empty |
| סוג משימה | color_mkwyq310 | `OdcanitCase.TikType` | Status column. Mapped via MapTaskTypeLabel(): Contains "פגיש" → "פגישה", Contains "מכתב"/"דריש" → "מכתב דרישה", Contains "זימון"/"דיון" → "זימון לדיון", Contains "הודע" → "הודעה", Default → "טיפול בתיק" |
| אחראי | text_mkxz6j9y | **Computed from OdcanitCase** | **Responsible Logic**: Referant → TeamName → TikOwner (as string). From DetermineResponsibleText() |

## Data Source Summary

### Odcanit Database Views/Tables

- **vwExportToOuterSystems_Files**: Base case data (TikNumber, TikCounter, tsCreateDate, etc.)
- **vwExportToOuterSystems_UserData**: Client contact info (phone, email, address)
- **vwSides**: Case sides (plaintiff, defendant, third party) with names, IDs, addresses
- **vwDiaryEvents**: Court hearings, dates, judge names, court information
- **vwExportToOuterSystems_UserData**: Policy holder, driver, vehicle, financial, and legal data via FieldName matching
- **vwHozlapFormsData_TikMainData**: Court case numbers (clcCourtNum, CourtName)

### Enrichment Process

1. **Client Enrichment**: Loads client contact info from vwExportToOuterSystems_UserData
2. **Sides Enrichment**: Loads plaintiff, defendant, third party from vwSides
3. **Diary Events Enrichment**: Loads court hearings and events from vwDiaryEvents
4. **User Data Enrichment**: Loads legal user data (UserData view vwExportToOuterSystems_UserData, PageName = "פרטי תיק נזיקין מליגל") and maps by FieldName
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

- Some columns may be resolved dynamically by title using MondayMetadataProvider (cached per board)
- Policy holder name is mapped to text_mky27a51 via `PolicyHolderNameColumnId` in MondaySettings

