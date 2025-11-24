using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Odmon.Worker.Models;

namespace Odmon.Worker.OdcanitAccess
{
    public class SqlOdcanitReader : IOdcanitReader
    {
        private readonly OdcanitDbContext _db;
        private readonly ILogger<SqlOdcanitReader> _logger;
        private const string DorScreenPageName = "מסך דור";
        private static readonly StringComparer HebrewComparer = StringComparer.Ordinal;
        private static readonly Dictionary<string, Action<OdcanitCase, OdcanitUserData>> UserDataFieldHandlers = BuildUserDataFieldHandlers();

        public SqlOdcanitReader(OdcanitDbContext db, ILogger<SqlOdcanitReader> logger)
        {
            _db = db;
            _logger = logger;
        }

        public async Task<List<OdcanitCase>> GetCasesCreatedOnDateAsync(DateTime date, CancellationToken ct)
        {
            var startDate = date.Date;
            var endDate = startDate.AddDays(1);

            var cases = await _db.Cases
                .AsNoTracking()
                .Where(c => c.tsCreateDate >= startDate && c.tsCreateDate < endDate)
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

            var clientsFromDb = await _db.Clients
                .AsNoTracking()
                .ToListAsync(ct);

            var clients = clientsFromDb
                .Where(x => sideCounterSet.Contains(x.SideCounter) && visualIdSet.Contains(x.VisualID))
                .ToList();

            var clientLookup = clients
                .GroupBy(x => new { x.SideCounter, x.VisualID })
                .ToDictionary(
                    g => (g.Key.SideCounter, g.Key.VisualID),
                    g => g.First());

            foreach (var odcanitCase in cases)
            {
                if (odcanitCase.ClientVisualID is null)
                {
                    continue;
                }

                if (!clientLookup.TryGetValue((odcanitCase.SideCounter, odcanitCase.ClientVisualID), out var client))
                {
                    continue;
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

            var tikCounterSet = new HashSet<int>(tikCounters);

            var sidesFromDb = await _db.Sides
                .AsNoTracking()
                .ToListAsync(ct);

            var sides = sidesFromDb
                .Where(s => tikCounterSet.Contains(s.TikCounter))
                .ToList();

            var sidesByCase = sides
                .GroupBy(s => s.TikCounter)
                .ToDictionary(g => g.Key, g => g.ToList());

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
                odcanitCase.CourtCaseNumber = hearing.IDInCourt ?? hearing.GlobCourtNum;
                odcanitCase.JudgeName = hearing.JudgeName;
                odcanitCase.HearingDate = hearing.StartDate;
                odcanitCase.HearingTime = hearing.FromTime?.TimeOfDay ?? hearing.ToTime?.TimeOfDay;
            }
        }

        private async Task EnrichWithUserDataAsync(List<OdcanitCase> cases, List<int> tikCounters, CancellationToken ct)
        {
            _logger.LogDebug("Enriching cases with Dor screen user data.");

            var tikCounterSet = new HashSet<int>(tikCounters);

            var userDataRowsFromDb = await _db.UserData
                .AsNoTracking()
                .Where(u => u.PageName == DorScreenPageName)
                .ToListAsync(ct);

            var userDataRows = userDataRowsFromDb
                .Where(u => tikCounterSet.Contains(u.TikCounter))
                .ToList();

            var userDataByCase = userDataRows
                .GroupBy(u => u.TikCounter)
                .ToDictionary(g => g.Key, g => g.ToList());

            foreach (var odcanitCase in cases)
            {
                if (!userDataByCase.TryGetValue(odcanitCase.TikCounter, out var rows))
                {
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
            Add("מספר רישוי נוסף", (c, row) => c.SecondCarNumber = row.strData);
            Add("מספר רישוי רכב ג'", (c, row) => c.ThirdPartyCarNumber = row.strData);
            Add("מספר רכב צד ג'", (c, row) => c.ThirdPartyCarNumber = row.strData);
            Add("סכום תביעה", (c, row) => c.RequestedClaimAmount = ExtractDecimal(row) ?? c.RequestedClaimAmount);
            Add("הסעד המבוקש ( סכום תביעה)", (c, row) => c.RequestedClaimAmount = ExtractDecimal(row) ?? c.RequestedClaimAmount);
            Add("סכום לתשלום", (c, row) => c.PaymentDueAmount = ExtractDecimal(row) ?? c.PaymentDueAmount);
            Add("ירידת ערך", (c, row) => c.LossOfValueAmount = ExtractDecimal(row) ?? c.LossOfValueAmount);
            Add("הפסדים", (c, row) => c.OtherLossesAmount = ExtractDecimal(row) ?? c.OtherLossesAmount);
            Add("שכ\"ט שמאי", (c, row) => c.AppraiserFeeAmount = ExtractDecimal(row) ?? c.AppraiserFeeAmount);
            Add("נזק ישיר", (c, row) => c.DirectDamageAmount = ExtractDecimal(row) ?? c.DirectDamageAmount);
            Add("סכום פסק דין", (c, row) => c.JudgmentAmount = ExtractDecimal(row) ?? c.JudgmentAmount);
            Add("סכום תביעה מוכח", (c, row) => c.ProvenClaimAmount = ExtractDecimal(row) ?? c.ProvenClaimAmount);
            Add("שם עד", (c, row) => c.WitnessName = row.strData);
            Add("סלולרי עד", (c, row) => c.WitnessPhone = row.strData);
            Add("שם נהג", (c, row) => c.DriverName = row.strData);
            Add("תעודת זהות נהג", (c, row) => c.DriverId = row.strData);
            Add("סלולרי נהג", (c, row) => c.DriverPhone = row.strData);
            Add("שם בעל פוליסה", (c, row) => c.PolicyHolderName = row.strData);
            Add("ת.ז. בעל פוליסה", (c, row) => c.PolicyHolderId = row.strData);
            Add("כתובת בעל פוליסה", (c, row) => c.PolicyHolderAddress = row.strData);
            Add("סלולרי בעל פוליסה", (c, row) => c.PolicyHolderPhone = row.strData);
            Add("כתובת דוא\"ל בעל פוליסה", (c, row) => c.PolicyHolderEmail = row.strData);
            Add("שם נהג צד ג'", (c, row) => c.ThirdPartyDriverName = row.strData);
            Add("ת.ז. נהג צד ג'", (c, row) => c.ThirdPartyDriverId = row.strData);
            Add("נייד צד ג'", (c, row) => c.ThirdPartyPhone = row.strData);
            Add("שם מעסיק צד ג'", (c, row) => c.ThirdPartyEmployerName = row.strData);
            Add("מספר זהות מעסיק צד ג'", (c, row) => c.ThirdPartyEmployerId = row.strData);
            Add("כתובת מעסיק צד ג'", (c, row) => c.ThirdPartyEmployerAddress = row.strData);
            Add("מיוצג על ידי עו\"ד צד ג'", (c, row) => c.ThirdPartyLawyerName = row.strData);
            Add("כתובת עו\"ד צד ג'", (c, row) => c.ThirdPartyLawyerAddress = row.strData);
            Add("טלפון עו\"ד צד ג'", (c, row) => c.ThirdPartyLawyerPhone = row.strData);
            Add("כתובת דוא\"ל עו\"ד צד ג'", (c, row) => c.ThirdPartyLawyerEmail = row.strData);
            Add("חברה מבטחת צד ג'", (c, row) => c.ThirdPartyInsurerName = row.strData);
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
            Add("מועד קבלת כתב התביעה", (c, row) => c.ComplaintReceivedDate = row.dateData ?? c.ComplaintReceivedDate);
            Add("folderID", (c, row) => c.CaseFolderId = row.strData);
            Add("שם עורך דין", (c, row) => c.AttorneyName = row.strData);
            Add("פקס", (c, row) => c.DefendantFax = row.strData);

            return dict;
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
    }
}

