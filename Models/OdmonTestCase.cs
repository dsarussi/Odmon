using System;
using System.Collections.Generic;

namespace Odmon.Worker.Models
{
    /// <summary>
    /// Test case entity for dbo.OdmonTestCases table (IntegrationDb).
    /// Properties match Hebrew column names with underscores.
    /// Used in TEST MODE when Testing.Source = IntegrationDbOdmonTestCases.
    /// </summary>
    public class OdmonTestCase
    {
        // Primary identification
        public int Id { get; set; }
        public int TikCounter { get; set; }
        public string מספר_תיק { get; set; } = string.Empty;
        public string? שם_תיק { get; set; }
        public string? סטטוס { get; set; }
        public string? סוג_תיק { get; set; }

        // Client information
        public string? שם_לקוח { get; set; }
        public string? מספר_לקוח { get; set; }
        public string? טלפון_לקוח { get; set; }
        public string? דוא_ל_לקוח { get; set; }
        public string? כתובת_לקוח { get; set; }
        public string? ח_פ_לקוח { get; set; }

        // Dates
        public DateTime? תאריך_פתיחת_תיק { get; set; }
        public DateTime? תאריך_פתיחה { get; set; }
        public DateTime? תאריך_עדכון { get; set; }
        public DateTime? תאריך_סגירה { get; set; }
        public DateTime? תאריך_אירוע { get; set; }
        public DateTime? מועד_קבלת_כתב_התביעה { get; set; }

        // Policy holder (בעל פוליסה)
        public string? שם_בעל_פוליסה { get; set; }
        public string? ת_ז_בעל_פוליסה { get; set; }
        public string? תעודת_זהות_בעל_פוליסה { get; set; }
        public string? כתובת_בעל_פוליסה { get; set; }
        public string? טלפון_בעל_פוליסה { get; set; }
        public string? סלולרי_בעל_פוליסה { get; set; }
        public string? כתובת_דוא_ל_בעל_פוליסה { get; set; }

        // Vehicle information
        public string? מספר_רישוי { get; set; }
        public string? מספר_רישוי_נוסף { get; set; }

        // Driver (נהג)
        public string? שם_נהג { get; set; }
        public string? תעודת_זהות_נהג { get; set; }
        public string? סלולרי_נהג { get; set; }

        // Witness
        public string? שם_עד { get; set; }

        // Plaintiff (תובע)
        public string? שם_תובע { get; set; }
        public string? ת_ז_תובע { get; set; }
        public string? כתובת_תובע { get; set; }
        public string? סלולרי_תובע { get; set; }
        public string? טלפון_תובע { get; set; }
        public string? כתובת_דוא_ל_תובע { get; set; }
        public string? צד_תובע { get; set; }

        // Defendant (נתבע)
        public string? שם_נתבע { get; set; }
        public string? צד_נתבע { get; set; }
        public string? פקס { get; set; }
        public string? נתבעים_נוספים { get; set; }

        // Third party (צד ג')
        public string? שם_נהג_צד_ג { get; set; }
        public string? ת_ז_נהג_צד_ג { get; set; }
        public string? מספר_רישוי_רכב_ג { get; set; }
        public string? מספר_רכב_צד_ג { get; set; }
        public string? נייד_צד_ג { get; set; }
        public string? חברה_מבטחת_צד_ג { get; set; }
        public string? שם_מעסיק_צד_ג { get; set; }
        public string? מספר_זהות_מעסיק_צד_ג { get; set; }
        public string? כתובת_מעסיק_צד_ג { get; set; }
        public string? מיוצג_על_ידי_עו_ד_צד_ג { get; set; }
        public string? כתובת_עו_ד_צד_ג { get; set; }
        public string? טלפון_עו_ד_צד_ג { get; set; }
        public string? כתובת_דוא_ל_עו_ד_צד_ג { get; set; }

        // Insurance
        public string? ח_פ_חברת_ביטוח { get; set; }
        public string? כתובת_חברת_ביטוח { get; set; }
        public string? כתובת_דוא_ל_חברת_ביטוח { get; set; }

        // Court information (בית משפט)
        public string? שם_בית_משפט { get; set; }
        public string? עיר_בית_משפט { get; set; }
        public string? מספר_תיק_בית_משפט { get; set; }
        public string? מספר_הליך_בית_משפט { get; set; }
        public string? שם_שופט { get; set; }
        public DateTime? תאריך_דיון { get; set; }
        public TimeSpan? שעה { get; set; }

        // Legal
        public string? שם_עורך_דין { get; set; }
        public string? כתובת_נתבע { get; set; }
        public string? מרחוב_הגנה { get; set; }
        public string? מרחוב_תביעה { get; set; }
        public string? folderID { get; set; }

        // Accident details
        public string? נסיבות_התאונה_בקצרה { get; set; }
        public string? גרסאות_תביעה { get; set; }

        // Financial amounts (סכומים)
        public decimal? הסעד_המבוקש_סכום_תביעה { get; set; }
        public decimal? סכום_תביעה { get; set; }
        public decimal? סכום_תביעה_מוכח { get; set; }
        public decimal? סכום_פסק_דין { get; set; }
        public decimal? שכ_ט_שמאי { get; set; }
        public decimal? נזק_ישיר { get; set; }
        public decimal? סכום_נזק_ישיר { get; set; }
        public decimal? הפסדים { get; set; }
        public decimal? ירידת_ערך { get; set; }
        public decimal? שווי_שרידים { get; set; }

        // Other
        public string? סוג_מסמך { get; set; }
        public string? מספר_תיק_חוזלא_פ { get; set; }
        public string? נוסף { get; set; }

        // Convert to OdcanitCase for compatibility with existing sync logic
        public OdcanitCase ToOdcanitCase()
        {
            return new OdcanitCase
            {
                TikCounter = TikCounter,
                TikNumber = מספר_תיק ?? string.Empty,
                TikName = שם_תיק ?? string.Empty,
                ClientName = שם_לקוח ?? string.Empty,
                ClientVisualID = מספר_לקוח,
                ClientPhone = טלפון_לקוח,
                ClientEmail = דוא_ל_לקוח,
                ClientAddress = כתובת_לקוח,
                ClientTaxId = ח_פ_לקוח,
                StatusName = סטטוס ?? string.Empty,
                TikType = סוג_תיק,
                tsCreateDate = תאריך_פתיחת_תיק ?? תאריך_פתיחה,
                tsModifyDate = תאריך_עדכון,
                TikCloseDate = תאריך_סגירה,
                EventDate = תאריך_אירוע,
                ComplaintReceivedDate = מועד_קבלת_כתב_התביעה,
                PolicyHolderName = שם_בעל_פוליסה,
                PolicyHolderId = ת_ז_בעל_פוליסה ?? תעודת_זהות_בעל_פוליסה,
                PolicyHolderAddress = כתובת_בעל_פוליסה,
                PolicyHolderPhone = טלפון_בעל_פוליסה ?? סלולרי_בעל_פוליסה,
                PolicyHolderEmail = כתובת_דוא_ל_בעל_פוליסה,
                MainCarNumber = מספר_רישוי,
                SecondCarNumber = מספר_רישוי_נוסף,
                DriverName = שם_נהג,
                DriverId = תעודת_זהות_נהג,
                DriverPhone = סלולרי_נהג,
                WitnessName = שם_עד,
                PlaintiffName = שם_תובע,
                PlaintiffId = ת_ז_תובע,
                PlaintiffAddress = כתובת_תובע,
                PlaintiffPhone = סלולרי_תובע ?? טלפון_תובע,
                PlaintiffEmail = כתובת_דוא_ל_תובע,
                PlaintiffSideRaw = צד_תובע,
                DefendantName = שם_נתבע,
                DefendantSideRaw = צד_נתבע,
                DefendantFax = פקס,
                AdditionalDefendants = נתבעים_נוספים,
                ThirdPartyDriverName = שם_נהג_צד_ג,
                ThirdPartyDriverId = ת_ז_נהג_צד_ג,
                ThirdPartyCarNumber = מספר_רישוי_רכב_ג ?? מספר_רכב_צד_ג,
                ThirdPartyPhone = נייד_צד_ג,
                ThirdPartyInsurerName = חברה_מבטחת_צד_ג,
                ThirdPartyEmployerName = שם_מעסיק_צד_ג,
                ThirdPartyEmployerId = מספר_זהות_מעסיק_צד_ג,
                ThirdPartyEmployerAddress = כתובת_מעסיק_צד_ג,
                ThirdPartyLawyerName = מיוצג_על_ידי_עו_ד_צד_ג,
                ThirdPartyLawyerAddress = כתובת_עו_ד_צד_ג,
                ThirdPartyLawyerPhone = טלפון_עו_ד_צד_ג,
                ThirdPartyLawyerEmail = כתובת_דוא_ל_עו_ד_צד_ג,
                InsuranceCompanyId = ח_פ_חברת_ביטוח,
                InsuranceCompanyAddress = כתובת_חברת_ביטוח,
                InsuranceCompanyEmail = כתובת_דוא_ל_חברת_ביטוח,
                CourtName = שם_בית_משפט,
                CourtCity = עיר_בית_משפט,
                CourtCaseNumber = מספר_תיק_בית_משפט ?? מספר_הליך_בית_משפט,
                JudgeName = שם_שופט,
                HearingDate = תאריך_דיון,
                HearingTime = שעה,
                AttorneyName = שם_עורך_דין,
                DefenseStreet = כתובת_נתבע ?? מרחוב_הגנה,
                ClaimStreet = מרחוב_תביעה,
                CaseFolderId = folderID,
                Notes = נסיבות_התאונה_בקצרה ?? גרסאות_תביעה,
                RequestedClaimAmount = הסעד_המבוקש_סכום_תביעה ?? סכום_תביעה,
                ProvenClaimAmount = סכום_תביעה_מוכח,
                JudgmentAmount = סכום_פסק_דין,
                AppraiserFeeAmount = שכ_ט_שמאי,
                DirectDamageAmount = נזק_ישיר ?? סכום_נזק_ישיר,
                OtherLossesAmount = הפסדים,
                LossOfValueAmount = ירידת_ערך,
                ResidualValueAmount = שווי_שרידים,
                DocumentType = סוג_מסמך,
                HozlapTikNumber = מספר_תיק_חוזלא_פ,
                Additional = נוסף
            };
        }
    }
}
