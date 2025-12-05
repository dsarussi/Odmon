using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Odmon.Worker.Models;

namespace Odmon.Worker.OdcanitAccess
{
    public class SqlOdcanitReader : IOdcanitReader
    {
        private readonly OdcanitDbContext _db;
        private readonly ILogger<SqlOdcanitReader> _logger;
        private readonly bool _isTestMode;
        private const string DorScreenPageName = "פרטי תיק נזיקין מליגל";
        private static readonly StringComparer HebrewComparer = StringComparer.Ordinal;
        private static readonly Dictionary<string, Action<OdcanitCase, OdcanitUserData>> UserDataFieldHandlers = BuildUserDataFieldHandlers();

        public SqlOdcanitReader(OdcanitDbContext db, ILogger<SqlOdcanitReader> logger, IConfiguration configuration)
        {
            _db = db;
            _logger = logger;
            var safetySection = configuration.GetSection("Safety");
            _isTestMode = safetySection.GetValue<bool>("TestMode", false);
        }

        public async Task<List<OdcanitCase>> GetCasesCreatedOnDateAsync(DateTime date, CancellationToken ct)
        {
            var startDate = date.Date;
            var endDate = startDate.AddDays(1);

            var casesQuery = _db.Cases
                .AsNoTracking();

            if (_isTestMode)
            {
                casesQuery = casesQuery.Where(c => c.TikNumber == "9/1329");
            }
            else
            {
                casesQuery = casesQuery.Where(c => c.tsCreateDate >= startDate && c.tsCreateDate < endDate);
            }

            var cases = await casesQuery
                .OrderBy(c => c.tsCreateDate)
                .ToListAsync(ct);

            if (!cases.Any())
            {
                return cases;
            }

            await EnrichWithClientsAsync(cases, ct);

            var tikCounters = cases
                .Select(c => c.TikCounter)
                .Distinct()
                .ToList();

            await EnrichWithSidesAsync(cases, tikCounters, ct);
            await EnrichWithDiaryEventsAsync(cases, tikCounters, ct);
            await EnrichWithUserDataAsync(cases, tikCounters, ct);
            await EnrichWithHozlapMainDataAsync(cases, tikCounters, ct);

            LogCaseEnrichment(cases);

            return cases;
        }

        private async Task EnrichWithClientsAsync(List<OdcanitCase> cases, CancellationToken ct)
        {
            _logger.LogDebug("Enriching {Count} cases with client contact info.", cases.Count);

            var sideCounters = cases
                .Select(c => c.SideCounter)
                .Distinct()
                .ToList();

            var visualIds = cases
                .Select(c => c.ClientVisualID)
                .Where(id => !string.IsNullOrEmpty(id))
                .Select(id => id!)
                .Distinct()
                .ToList();

            var sideCounterSet = new HashSet<int>(sideCounters);
            var visualIdSet = new HashSet<string>(visualIds);

            _logger.LogDebug("Loading clients for {SideCounters} side counters and {VisualIds} visual IDs.", sideCounterSet.Count, visualIdSet.Count);

            var clientsFromDb = await _db.Clients
                .AsNoTracking()
                .ToListAsync(ct);

            _logger.LogDebug("Loaded {TotalClients} client rows from view.", clientsFromDb.Count);

            var clients = clientsFromDb
                .Where(x => sideCounterSet.Contains(x.SideCounter) && visualIdSet.Contains(x.VisualID))
                .ToList();

            _logger.LogDebug("Matched {MatchedClients} client rows to cases.", clients.Count);

            var clientLookup = clients
                .GroupBy(x => new { x.SideCounter, x.VisualID })
                .ToDictionary(
                    g => (g.Key.SideCounter, g.Key.VisualID),
                    g => g.First());

            foreach (var odcanitCase in cases)
            {
                if (odcanitCase.ClientVisualID is null || !clientLookup.TryGetValue((odcanitCase.SideCounter, odcanitCase.ClientVisualID), out var client))
                {
                    _logger.LogDebug(
                        "No client row found for TikCounter {TikCounter}, SideCounter {SideCounter}, ClientVisualID {ClientVisualID}",
                        odcanitCase.TikCounter,
                        odcanitCase.SideCounter,
                        odcanitCase.ClientVisualID ?? "<null>");
                    continue;
                }

                if (odcanitCase.TikCounter == 31577)
                {
                    _logger.LogDebug("Client data for TikCounter 31577: SideCounter={SideCounter}, VisualID={VisualID}, Phone={Phone}, Email={Email}",
                        client.SideCounter,
                        client.VisualID,
                        client.Mobile,
                        client.Email);
                }

                odcanitCase.ClientPhone = client.Mobile;
                odcanitCase.ClientEmail = client.Email;
                odcanitCase.ClientAddress = client.FullAddress;
                odcanitCase.ComplaintReceivedDate = odcanitCase.HozlapOpenDate;
            }
        }

        private async Task EnrichWithSidesAsync(List<OdcanitCase> cases, List<int> tikCounters, CancellationToken ct)
        {
            _logger.LogDebug("Enriching cases with sides from vwSides.");

            var tikCounterSet = new HashSet<long>(tikCounters.Select(tc => (long)tc));

            var sidesFromDb = await _db.Sides
                .AsNoTracking()
                .ToListAsync(ct);

            _logger.LogDebug("Loaded {TotalSides} sides rows from view.", sidesFromDb.Count);

            var sides = sidesFromDb
                .Where(s => tikCounterSet.Contains(s.TikCounter))
                .ToList();

            _logger.LogDebug("Matched {MatchedSides} sides rows to current TikCounters.", sides.Count);

            var sidesByCase = sides
                .GroupBy(s => s.TikCounter)
                .ToDictionary(g => g.Key, g => g.ToList());

            if (sidesByCase.TryGetValue(31577, out var tik31577Sides))
            {
                var roleSummary = tik31577Sides
                    .Select(s => new { s.SideTypeCode, s.SideTypeName, s.FullName, s.ID })
                    .ToList();
                _logger.LogDebug("TikCounter 31577 sides: {@Sides}", roleSummary);
            }

            foreach (var odcanitCase in cases)
            {
                if (!sidesByCase.TryGetValue(odcanitCase.TikCounter, out var caseSides))
                {
                    continue;
                }

                var plaintiff = caseSides.FirstOrDefault(s => DetermineSideRole(s.SideTypeName) == SideRole.Plaintiff);
                if (plaintiff != null)
                {
                    odcanitCase.PlaintiffName = plaintiff.FullName;
                    odcanitCase.PlaintiffId = plaintiff.ID;
                    odcanitCase.PlaintiffAddress = plaintiff.FullAddress;
                }

                var defendant = caseSides.FirstOrDefault(s => DetermineSideRole(s.SideTypeName) == SideRole.Defendant);
                if (defendant != null)
                {
                    odcanitCase.DefendantName = defendant.FullName;
                    odcanitCase.DefendantId = defendant.ID;
                    odcanitCase.DefendantAddress = defendant.FullAddress;
                }

                var thirdParty = caseSides.FirstOrDefault(s => DetermineSideRole(s.SideTypeName) == SideRole.ThirdParty);
                if (thirdParty != null)
                {
                    odcanitCase.ThirdPartyDriverName = thirdParty.FullName;
                    odcanitCase.ThirdPartyDriverId = thirdParty.ID;
                }
            }
        }

        private async Task EnrichWithDiaryEventsAsync(List<OdcanitCase> cases, List<int> tikCounters, CancellationToken ct)
        {
            _logger.LogDebug("Enriching cases with diary events.");

            var tikCounterSet = new HashSet<int>(tikCounters);

            var diaryEventsFromDb = await _db.DiaryEvents
                .AsNoTracking()
                .ToListAsync(ct);

            var diaryEvents = diaryEventsFromDb
                .Where(d => d.TikCounter.HasValue && tikCounterSet.Contains(d.TikCounter.Value))
                .ToList();

            _logger.LogDebug("Loaded {TotalDiary} diary rows; matched {MatchedDiary} to cases.", diaryEventsFromDb.Count, diaryEvents.Count);

            var diaryByCase = diaryEvents
                .GroupBy(d => d.TikCounter!.Value)
                .ToDictionary(g => g.Key, g => g
                    .Where(e => e != null)
                    .OrderBy(e => e.StartDate ?? DateTime.MaxValue)
                    .ToList());

            foreach (var odcanitCase in cases)
            {
                if (!diaryByCase.TryGetValue(odcanitCase.TikCounter, out var events))
                {
                    continue;
                }

                var hearing = events.FirstOrDefault(e =>
                    (!string.IsNullOrWhiteSpace(e.CourtName) || !string.IsNullOrWhiteSpace(e.IDInCourt)) &&
                    e.StartDate.HasValue);

                if (hearing == null)
                {
                    hearing = events.FirstOrDefault();
                }

                if (hearing == null)
                {
                    continue;
                }

                odcanitCase.CourtName = hearing.CourtName ?? hearing.CourtCodeName;
                odcanitCase.CourtCity = hearing.City;
                odcanitCase.JudgeName = hearing.JudgeName;
                odcanitCase.HearingDate = hearing.StartDate;
                odcanitCase.HearingTime = hearing.FromTime?.TimeOfDay ?? hearing.ToTime?.TimeOfDay;
            }
        }

        private async Task EnrichWithHozlapMainDataAsync(List<OdcanitCase> cases, List<int> tikCounters, CancellationToken ct)
        {
            _logger.LogDebug("Enriching cases with Hozlap main data for court case numbers.");

            var tikCounterSet = new HashSet<int>(tikCounters);

            var hozlapDataFromDb = await _db.HozlapMainData
                .AsNoTracking()
                .Where(h => tikCounterSet.Contains(h.TikCounter))
                .ToListAsync(ct);

            _logger.LogDebug("Loaded {TotalHozlap} Hozlap main data rows.", hozlapDataFromDb.Count);

            var hozlapByCase = hozlapDataFromDb
                .GroupBy(h => h.TikCounter)
                .ToDictionary(g => g.Key, g => g.First());

            foreach (var odcanitCase in cases)
            {
                if (!hozlapByCase.TryGetValue(odcanitCase.TikCounter, out var hozlapData))
                {
                    continue;
                }

                // Build court case number from clcCourtNum + CourtName
                var courtCaseDisplay = string.IsNullOrWhiteSpace(hozlapData.clcCourtNum)
                    ? hozlapData.CourtName
                    : $"{hozlapData.clcCourtNum} {hozlapData.CourtName}";

                if (!string.IsNullOrWhiteSpace(courtCaseDisplay))
                {
                    odcanitCase.CourtCaseNumber = courtCaseDisplay.Trim();
                }
            }
        }

        private async Task EnrichWithUserDataAsync(List<OdcanitCase> cases, List<int> tikCounters, CancellationToken ct)
        {
            _logger.LogDebug("Enriching cases with Dor screen user data.");

            var tikCounterSet = new HashSet<int>(tikCounters);

            var userDataRows = await _db.UserData
                .AsNoTracking()
                .Where(u => tikCounterSet.Contains(u.TikCounter))
                .ToListAsync(ct);

            var dorRows = userDataRows
                .Where(u => u.PageName == DorScreenPageName)
                .ToList();

            _logger.LogDebug(
                "Loaded {TotalUserData} rows for TikCounters, Dor screen rows={DorRows}, Distinct pages={Pages}",
                userDataRows.Count,
                dorRows.Count,
                userDataRows.Select(u => u.PageName).Distinct().ToArray());

            var userDataAllByCase = userDataRows
                .GroupBy(u => u.TikCounter)
                .ToDictionary(g => g.Key, g => g.ToList());

            var userDataByCase = dorRows
                .GroupBy(u => u.TikCounter)
                .ToDictionary(g => g.Key, g => g.ToList());

            if (userDataAllByCase.TryGetValue(31577, out var tik31577Rows))
            {
                var pairs = tik31577Rows
                    .Select(r => new { r.PageName, r.FieldName, r.strData, r.numData, r.dateData })
                    .ToList();
                _logger.LogDebug("TikCounter 31577 user data rows: {@UserFields}", pairs);
            }

            foreach (var odcanitCase in cases)
            {
                var tikCounterKey = odcanitCase.TikCounter;

                if (!userDataByCase.TryGetValue(tikCounterKey, out var rows))
                {
                    if (userDataAllByCase.TryGetValue(tikCounterKey, out var allRows))
                    {
                        var availablePages = allRows.Select(r => r.PageName ?? "<null>").Distinct().ToArray();
                        _logger.LogDebug("No Dor page rows for TikCounter {TikCounter}. Available PageNames: {Pages}", odcanitCase.TikCounter, availablePages);
                    }
                    else
                    {
                        _logger.LogDebug("No user data rows at all for TikCounter {TikCounter}.", odcanitCase.TikCounter);
                    }
                    continue;
                }

                foreach (var row in rows)
                {
                    var key = NormalizeUserFieldName(row.FieldName);
                    if (key == null)
                    {
                        continue;
                    }

                    if (UserDataFieldHandlers.TryGetValue(key, out var handler))
                    {
                        handler(odcanitCase, row);
                    }
                }
            }
        }

        private static string? NormalizeUserFieldName(string? fieldName)
        {
            if (string.IsNullOrWhiteSpace(fieldName))
            {
                return null;
            }

            return fieldName
                .Replace('’', '\'')
                .Replace('״', '"')
                .Trim();
        }

        private static Dictionary<string, Action<OdcanitCase, OdcanitUserData>> BuildUserDataFieldHandlers()
        {
            var dict = new Dictionary<string, Action<OdcanitCase, OdcanitUserData>>(HebrewComparer);

            void Add(string key, Action<OdcanitCase, OdcanitUserData> handler)
            {
                var normalized = NormalizeUserFieldName(key);
                if (normalized != null)
                {
                    dict[normalized] = handler;
                }
            }

            Add("מספר רישוי", (c, row) => c.MainCarNumber = row.strData);
            Add("Main car number", (c, row) => c.MainCarNumber = row.strData);
            Add("Driver: main car number", (c, row) => c.MainCarNumber = row.strData);
            Add("מספר רישוי נוסף", (c, row) => c.SecondCarNumber = row.strData);
            Add("מספר רישוי רכב ג'", (c, row) => c.ThirdPartyCarNumber = row.strData);
            Add("מספר רכב צד ג'", (c, row) => c.ThirdPartyCarNumber = row.strData);
            Add("Third-party driver: car number", (c, row) => c.ThirdPartyCarNumber = row.strData);
            Add("סכום תביעה", (c, row) => c.RequestedClaimAmount = ExtractDecimal(row) ?? c.RequestedClaimAmount);
            Add("הסעד המבוקש ( סכום תביעה)", (c, row) => c.RequestedClaimAmount = ExtractDecimal(row) ?? c.RequestedClaimAmount);
            Add("Claim amount", (c, row) => c.RequestedClaimAmount = ExtractDecimal(row) ?? c.RequestedClaimAmount);
            Add("Claim amount (סכום תביעה)", (c, row) => c.RequestedClaimAmount = ExtractDecimal(row) ?? c.RequestedClaimAmount);
            Add("סכום לתשלום", (c, row) => c.PaymentDueAmount = ExtractDecimal(row) ?? c.PaymentDueAmount);
            Add("Amount to pay", (c, row) => c.PaymentDueAmount = ExtractDecimal(row) ?? c.PaymentDueAmount);
            Add("ירידת ערך", (c, row) => c.LossOfValueAmount = ExtractDecimal(row) ?? c.LossOfValueAmount);
            Add("Loss of value", (c, row) => c.LossOfValueAmount = ExtractDecimal(row) ?? c.LossOfValueAmount);
            Add("הפסדים", (c, row) => c.OtherLossesAmount = ExtractDecimal(row) ?? c.OtherLossesAmount);
            Add("שכ\"ט שמאי", (c, row) => c.AppraiserFeeAmount = ExtractDecimal(row) ?? c.AppraiserFeeAmount);
            Add("נזק ישיר", (c, row) => c.DirectDamageAmount = ExtractDecimal(row) ?? c.DirectDamageAmount);
            Add("סכום פסק דין", (c, row) => c.JudgmentAmount = ExtractDecimal(row) ?? c.JudgmentAmount);
            Add("סכום תביעה מוכח", (c, row) => c.ProvenClaimAmount = ExtractDecimal(row) ?? c.ProvenClaimAmount);
            Add("אגרת בית משפט ( I+II )", (c, row) => c.CourtFeeTotal = ExtractDecimal(row) ?? c.CourtFeeTotal);
            Add("Court fee", (c, row) => c.CourtFeeTotal = ExtractDecimal(row) ?? c.CourtFeeTotal);
            Add("Court fee (I+II)", (c, row) => c.CourtFeeTotal = ExtractDecimal(row) ?? c.CourtFeeTotal);
            Add("מ.אגרה I", (c, row) => c.CourtFeePartOne = ExtractDecimal(row) ?? c.CourtFeePartOne);
            Add("שם עד", (c, row) => c.WitnessName = row.strData);
            Add("סלולרי עד", (c, row) => c.DriverMobile = row.strData);
            Add("שם נהג", (c, row) => c.DriverName = row.strData);
            Add("Driver: name", (c, row) => c.DriverName = row.strData);
            Add("תעודת זהות נהג", (c, row) => c.DriverId = row.strData);
            Add("Driver: id", (c, row) => c.DriverId = row.strData);
            Add("סלולרי נהג", (c, row) => c.DriverPhone = row.strData);
            Add("Driver: phone", (c, row) => c.DriverPhone = row.strData);
            Add("שם בעל פוליסה", (c, row) => c.PolicyHolderName = row.strData);
            Add("Policy holder: name", (c, row) => c.PolicyHolderName = row.strData);
            Add("ת.ז. בעל פוליסה", (c, row) => c.PolicyHolderId = row.strData);
            Add("תעודת זהות בעל פוליסה", (c, row) => c.PolicyHolderId = row.strData);
            Add("Policy holder: id", (c, row) => c.PolicyHolderId = row.strData);
            Add("כתובת בעל פוליסה", (c, row) => c.PolicyHolderAddress = row.strData);
            Add("Policy holder: address", (c, row) => c.PolicyHolderAddress = row.strData);
            Add("סלולרי בעל פוליסה", (c, row) => c.PolicyHolderPhone = row.strData);
            Add("Policy holder: phone", (c, row) => c.PolicyHolderPhone = row.strData);
            Add("כתובת דוא\"ל בעל פוליסה", (c, row) => c.PolicyHolderEmail = row.strData);
            Add("Policy holder: email", (c, row) => c.PolicyHolderEmail = row.strData);
            Add("שם נהג צד ג'", (c, row) => c.ThirdPartyDriverName = row.strData);
            Add("Third-party driver: name", (c, row) => c.ThirdPartyDriverName = row.strData);
            Add("ת.ז. נהג צד ג'", (c, row) => c.ThirdPartyDriverId = row.strData);
            Add("Third-party driver: id", (c, row) => c.ThirdPartyDriverId = row.strData);
            Add("נייד צד ג'", (c, row) => c.ThirdPartyPhone = row.strData);
            Add("Third-party driver: phone", (c, row) => c.ThirdPartyPhone = row.strData);
            Add("שם מעסיק צד ג'", (c, row) => c.ThirdPartyEmployerName = row.strData);
            Add("מספר זהות מעסיק צד ג'", (c, row) => c.ThirdPartyEmployerId = row.strData);
            Add("כתובת מעסיק צד ג'", (c, row) => c.ThirdPartyEmployerAddress = row.strData);
            Add("מיוצג על ידי עו\"ד צד ג'", (c, row) => c.ThirdPartyLawyerName = row.strData);
            Add("כתובת עו\"ד צד ג'", (c, row) => c.ThirdPartyLawyerAddress = row.strData);
            Add("טלפון עו\"ד צד ג'", (c, row) => c.ThirdPartyLawyerPhone = row.strData);
            Add("כתובת דוא\"ל עו\"ד צד ג'", (c, row) => c.ThirdPartyLawyerEmail = row.strData);
            Add("חברה מבטחת צד ג'", (c, row) => c.ThirdPartyInsurerName = row.strData);
            Add("Third-party driver: insurer name", (c, row) => c.ThirdPartyInsurerName = row.strData);
            Add("ח.פ. חברת ביטוח", (c, row) => c.InsuranceCompanyId = row.strData);
            Add("כתובת חברת ביטוח", (c, row) => c.InsuranceCompanyAddress = row.strData);
            Add("כתובת דוא\"ל חברת ביטוח", (c, row) => c.InsuranceCompanyEmail = row.strData);
            Add("ח.פ. לקוח", (c, row) => c.ClientTaxId = row.strData);
            Add("נתבעים נוספים", (c, row) => c.AdditionalDefendants = row.strData);
            Add("שם מוסך", (c, row) => c.GarageName = row.strData);
            Add("שם תובע", (c, row) => c.PlaintiffName = row.strData);
            Add("ת.ז. תובע", (c, row) => c.PlaintiffId = row.strData);
            Add("כתובת תובע", (c, row) => c.PlaintiffAddress = row.strData);
            Add("סלולרי תובע", (c, row) => c.PlaintiffPhone = row.strData);
            Add("כתובת דוא\"ל תובע", (c, row) => c.PlaintiffEmail = row.strData);
            Add("שם נתבע", (c, row) => c.DefendantName = row.strData);
            Add("פקס", (c, row) => c.DefendantFax = row.strData);
            Add("מרחוב (תביעה)", (c, row) => c.ClaimStreet = row.strData);
            Add("מרחוב (הגנה)", (c, row) => c.DefenseStreet = row.strData);
            Add("Event date", (c, row) => c.EventDate = ExtractDate(row) ?? c.EventDate);
            Add("תאריך אירוע", (c, row) => c.EventDate = ExtractDate(row) ?? c.EventDate);
            Add("מועד קבלת כתב התביעה", (c, row) => c.ComplaintReceivedDate = row.dateData ?? c.ComplaintReceivedDate);
            Add("folderID", (c, row) => c.CaseFolderId = row.strData);
            Add("שם עורך דין", (c, row) => c.AttorneyName = row.strData);
            Add("פקס", (c, row) => c.DefendantFax = row.strData);

            return dict;
        }

        private static DateTime? ExtractDate(OdcanitUserData row)
        {
            if (row.dateData.HasValue)
            {
                return row.dateData;
            }

            if (DateTime.TryParse(row.strData, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var parsed))
            {
                return parsed;
            }

            if (DateTime.TryParse(row.strData, CultureInfo.CurrentCulture, DateTimeStyles.AssumeLocal, out parsed))
            {
                return parsed;
            }

            return null;
        }

        private static decimal? ExtractDecimal(OdcanitUserData row)
        {
            if (row.numData.HasValue)
            {
                return Convert.ToDecimal(row.numData.Value, CultureInfo.InvariantCulture);
            }

            if (decimal.TryParse(row.strData, NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed))
            {
                return parsed;
            }

            return null;
        }

        private static SideRole DetermineSideRole(string? sideTypeName)
        {
            if (string.IsNullOrWhiteSpace(sideTypeName))
            {
                return SideRole.Unknown;
            }

            var normalized = sideTypeName.Trim();

            if (normalized.Contains("צד ג", StringComparison.OrdinalIgnoreCase))
            {
                return SideRole.ThirdParty;
            }

            if (normalized.Contains("תובע", StringComparison.OrdinalIgnoreCase))
            {
                return SideRole.Plaintiff;
            }

            if (normalized.Contains("נתבע", StringComparison.OrdinalIgnoreCase))
            {
                return SideRole.Defendant;
            }

            return SideRole.Unknown;
        }

        private enum SideRole
        {
            Unknown,
            Plaintiff,
            Defendant,
            ThirdParty
        }

        private void LogCaseEnrichment(IEnumerable<OdcanitCase> cases)
        {
            foreach (var c in cases)
            {
                _logger.LogDebug(
                    "Enriched Odcanit case {@Case}",
                    new
                    {
                        c.TikCounter,
                        c.TikNumber,
                        c.TikName,
                        c.ClientVisualID,
                        c.ClientName,
                        c.ClientPhone,
                        c.ClientEmail,
                        c.PolicyHolderName,
                        c.PolicyHolderId,
                        c.PolicyHolderAddress,
                        c.PolicyHolderPhone,
                        c.PolicyHolderEmail,
                        c.DriverName,
                        c.DriverId,
                        c.DriverPhone,
                        ThirdPartyName = c.ThirdPartyDriverName,
                        ThirdPartyId = c.ThirdPartyDriverId,
                        ThirdPartyCarNumber = c.ThirdPartyCarNumber,
                        c.ThirdPartyPhone,
                        c.ThirdPartyInsurerName,
                        c.PlaintiffName,
                        c.PlaintiffId,
                        c.PlaintiffAddress,
                        c.PlaintiffPhone,
                        c.PlaintiffEmail,
                        c.DefendantName,
                        c.DefendantAddress,
                        c.CourtName,
                        c.CourtCity,
                        c.CourtCaseNumber,
                        c.JudgeName,
                        c.HearingDate,
                        HearingHour = c.HearingTime?.ToString(),
                        c.EventDate,
                        c.RequestedClaimAmount,
                        c.ProvenClaimAmount,
                        c.JudgmentAmount,
                        CourtFeeTotal = c.CourtFeeTotal,
                        CourtFeePartOne = c.CourtFeePartOne
                    });
            }
        }
    }
}

