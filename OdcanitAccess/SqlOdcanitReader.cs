using System;
using System.Collections.Generic;
using System.Data;
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
        private const string LegalUserDataPageName = "פרטי תיק נזיקין מליגל";
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
            var casesQuery = _db.Cases
                .AsNoTracking();

            // Apply date filter unless date is DateTime.MinValue (which means "load all")
            if (date != DateTime.MinValue)
            {
                var startDate = date.Date;
                var endDate = startDate.AddDays(1);
                casesQuery = casesQuery.Where(c =>
                    c.tsCreateDate.HasValue &&
                    c.tsCreateDate.Value >= startDate &&
                    c.tsCreateDate.Value < endDate);
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

        public async Task<List<OdcanitCase>> GetCasesByTikCountersAsync(IEnumerable<int> tikCounters, CancellationToken ct)
        {
            var tikCounterList = tikCounters?.Distinct().ToList() ?? new List<int>();

            if (tikCounterList.Count == 0)
            {
                _logger.LogWarning("GetCasesByTikCountersAsync called with empty TikCounter list. Returning empty result.");
                return new List<OdcanitCase>();
            }

            _logger.LogDebug("Fetching Odcanit cases by TikCounter from vwExportToOuterSystems_Files for TikCounters: {TikCounters}", string.Join(", ", tikCounterList));

            // Fetch cases ONLY by TikCounter - NO date filters
            var cases = await _db.Cases
                .AsNoTracking()
                .Where(c => tikCounterList.Contains(c.TikCounter))
                .ToListAsync(ct);

            if (!cases.Any())
            {
                _logger.LogInformation("No cases found for TikCounters: {TikCounters}", string.Join(", ", tikCounterList));
                return cases;
            }

            _logger.LogInformation("Loaded {Count} cases by TikCounter from Odcanit: {TikCounters}", cases.Count, string.Join(", ", tikCounterList));

            await EnrichWithClientsAsync(cases, ct);

            var tikCountersForEnrichment = cases
                .Select(c => c.TikCounter)
                .Distinct()
                .ToList();

            await EnrichWithSidesAsync(cases, tikCountersForEnrichment, ct);
            await EnrichWithDiaryEventsAsync(cases, tikCountersForEnrichment, ct);
            await EnrichWithUserDataAsync(cases, tikCountersForEnrichment, ct);
            await EnrichWithHozlapMainDataAsync(cases, tikCountersForEnrichment, ct);

            LogCaseEnrichment(cases);

            return cases;
        }

        public async Task<List<OdcanitDiaryEvent>> GetDiaryEventsByTikCountersAsync(IEnumerable<int> tikCounters, CancellationToken ct)
        {
            var list = tikCounters?.Distinct().ToList() ?? new List<int>();
            if (list.Count == 0)
            {
                return new List<OdcanitDiaryEvent>();
            }

            var set = new HashSet<int>(list);
            var rows = await _db.DiaryEvents
                .AsNoTracking()
                .Where(d => d.TikCounter.HasValue && set.Contains(d.TikCounter.Value))
                .ToListAsync(ct);

            _logger.LogDebug("GetDiaryEventsByTikCountersAsync: loaded {Count} rows from vwExportToOuterSystems_YomanData for {TikCount} TikCounters.", rows.Count, list.Count);
            return rows;
        }

        public async Task<Dictionary<string, int>> ResolveTikNumbersToCountersAsync(IEnumerable<string> tikNumbers, CancellationToken ct)
        {
            var tikNumbersList = tikNumbers?
                .Where(tn => !string.IsNullOrWhiteSpace(tn))
                .Select(tn => tn.Trim())
                .Distinct(StringComparer.Ordinal)
                .ToList() ?? new List<string>();
            
            if (!tikNumbersList.Any())
            {
                return new Dictionary<string, int>(StringComparer.Ordinal);
            }

            _logger.LogInformation(
                "Resolving {Count} TikNumber(s) to TikCounters: [{TikNumbers}]",
                tikNumbersList.Count,
                string.Join(", ", tikNumbersList));

            var resolved = new Dictionary<string, int>(StringComparer.Ordinal);

            // Build parameterized SQL with explicit @p0, @p1, ... parameters
            var paramNames = new List<string>();
            var parameters = new List<Microsoft.Data.SqlClient.SqlParameter>();
            
            for (int i = 0; i < tikNumbersList.Count; i++)
            {
                var paramName = $"@p{i}";
                paramNames.Add(paramName);
                parameters.Add(new Microsoft.Data.SqlClient.SqlParameter(paramName, tikNumbersList[i]));
            }

            var sql = $"SELECT TikNumber, TikCounter FROM dbo.vwExportToOuterSystems_Files WHERE TikNumber IN ({string.Join(", ", paramNames)})";

            var connection = _db.Database.GetDbConnection();
            var wasOpen = connection.State == ConnectionState.Open;
            if (!wasOpen)
            {
                await connection.OpenAsync(ct);
            }

            try
            {
                using var command = connection.CreateCommand();
                command.CommandText = sql;
                command.CommandType = CommandType.Text;
                
                foreach (var param in parameters)
                {
                    command.Parameters.Add(param);
                }

                using var reader = await command.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                {
                    var tikNumber = reader.GetString(0);
                    var tikCounter = reader.GetInt32(1);
                    
                    if (!string.IsNullOrWhiteSpace(tikNumber))
                    {
                        resolved[tikNumber] = tikCounter;
                        _logger.LogDebug(
                            "Resolved TikNumber '{TikNumber}' -> TikCounter {TikCounter}",
                            tikNumber,
                            tikCounter);
                    }
                }
            }
            finally
            {
                if (!wasOpen && connection.State == ConnectionState.Open)
                {
                    await connection.CloseAsync();
                }
            }

            // Log unresolved TikNumbers
            foreach (var tikNumber in tikNumbersList)
            {
                if (!resolved.ContainsKey(tikNumber))
                {
                    _logger.LogWarning(
                        "TikNumber '{TikNumber}' could not be resolved to a TikCounter in Odcanit DB",
                        tikNumber);
                }
            }

            _logger.LogInformation(
                "Resolved {ResolvedCount} of {TotalCount} TikNumbers",
                resolved.Count,
                tikNumbersList.Count);

            return resolved;
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
            _logger.LogDebug("Enriching cases with upcoming hearings from vwExportToOuterSystems_YomanData (all statuses).");

            var tikCounterSet = new HashSet<int>(tikCounters);
            var now = DateTime.Now;

            // Load ALL future hearings (any MeetStatus) so we can track canceled/transferred status
            var diaryEventsQuery = _db.DiaryEvents
                .AsNoTracking()
                .Where(d =>
                    d.TikCounter.HasValue &&
                    tikCounterSet.Contains(d.TikCounter.Value) &&
                    d.StartDate.HasValue &&
                    d.StartDate.Value >= now);

            var diaryEvents = await diaryEventsQuery.ToListAsync(ct);

            _logger.LogDebug("Loaded {MatchedDiary} upcoming diary rows (all statuses) for current TikCounters.", diaryEvents.Count);

            var groupedByTik = diaryEvents
                .GroupBy(d => d.TikCounter!.Value)
                .ToDictionary(g => g.Key, g => g.ToList());

            foreach (var odcanitCase in cases)
            {
                if (!groupedByTik.TryGetValue(odcanitCase.TikCounter, out var hearingsForCase))
                {
                    continue;
                }

                // Select ONE diary row:
                //   Priority 1: nearest future active hearing (MeetStatus=0)
                //   Priority 2: nearest future hearing of any status
                var selected = hearingsForCase
                    .Where(e => e.MeetStatus == 0)
                    .OrderBy(e => e.StartDate!.Value)
                    .FirstOrDefault();

                selected ??= hearingsForCase
                    .OrderBy(e => e.StartDate!.Value)
                    .First();

                // Populate hearing-specific fields from the selected diary row.
                // These are the AUTHORITATIVE source for hearing gating / checksum / Monday updates.
                odcanitCase.HearingJudgeName = selected.JudgeName;
                odcanitCase.HearingCourtName = selected.CourtName ?? selected.CourtCodeName;
                odcanitCase.HearingCity = selected.City;
                odcanitCase.HearingStartDate = selected.StartDate;
                odcanitCase.HearingDate = selected.StartDate?.Date;
                odcanitCase.HearingTime = selected.StartDate?.TimeOfDay;
                odcanitCase.MeetStatus = selected.MeetStatus;

                // Also populate the general court fields from the selected row for backward
                // compatibility (CourtName status label, CourtCaseNumber, general JudgeName display).
                // Hozlap enrichment may override these later, which is fine — hearing-specific
                // fields above are NOT affected by Hozlap.
                odcanitCase.CourtName = selected.CourtName ?? selected.CourtCodeName;
                odcanitCase.CourtCity = selected.City;
                odcanitCase.JudgeName = selected.JudgeName;

                _logger.LogDebug(
                    "Selected diary row for TikCounter={TikCounter}: StartDate={StartDate}, MeetStatus={MeetStatus}, JudgeName='{JudgeName}', CourtName='{CourtName}', City='{City}'",
                    odcanitCase.TikCounter,
                    selected.StartDate?.ToString("yyyy-MM-dd HH:mm") ?? "<null>",
                    selected.MeetStatus?.ToString() ?? "<null>",
                    selected.JudgeName ?? "<null>",
                    (selected.CourtName ?? selected.CourtCodeName) ?? "<null>",
                    selected.City ?? "<null>");

                // Incorporate hearing modification time from ALL hearings into the case version
                // so that tsModifyDate-based change detection also catches hearing changes.
                var latestModify = hearingsForCase
                    .Where(e => e.tsModifyDate.HasValue)
                    .Select(e => e.tsModifyDate!.Value)
                    .DefaultIfEmpty()
                    .Max();

                if (latestModify != default)
                {
                    if (!odcanitCase.tsModifyDate.HasValue || latestModify > odcanitCase.tsModifyDate.Value)
                    {
                        odcanitCase.tsModifyDate = latestModify;
                    }
                }
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

                // Map court case number and court name independently from Hozlap
                if (!string.IsNullOrWhiteSpace(hozlapData.clcCourtNum))
                {
                    var raw = hozlapData.clcCourtNum;
                    var normalizedCaseNumber = NormalizeCourtCaseNumberAndCity(raw, out var derivedCity);
                    if (!string.IsNullOrWhiteSpace(normalizedCaseNumber))
                    {
                        odcanitCase.CourtCaseNumber = normalizedCaseNumber;
                    }

                    if (!string.IsNullOrWhiteSpace(derivedCity) && string.IsNullOrWhiteSpace(odcanitCase.CourtCity))
                    {
                        odcanitCase.CourtCity = derivedCity;
                    }

                    if (!string.IsNullOrWhiteSpace(derivedCity))
                    {
                        _logger.LogDebug(
                            "Split CourtCaseNumber+City from Hozlap for TikCounter {TikCounter}: raw='{Raw}', caseNumber='{CaseNumber}', city='{City}'",
                            odcanitCase.TikCounter,
                            raw ?? "<null>",
                            normalizedCaseNumber ?? "<null>",
                            derivedCity);
                    }
                }

                if (!string.IsNullOrWhiteSpace(hozlapData.CourtName))
                {
                    odcanitCase.CourtName = hozlapData.CourtName.Trim();

                    if (string.IsNullOrWhiteSpace(odcanitCase.CourtCity))
                    {
                        var cityFromName = DeriveCourtCityFromCourtName(odcanitCase.CourtName);
                        if (!string.IsNullOrWhiteSpace(cityFromName))
                        {
                            odcanitCase.CourtCity = cityFromName;
                        }
                    }
                }
            }
        }

        private async Task EnrichWithUserDataAsync(List<OdcanitCase> cases, List<int> tikCounters, CancellationToken ct)
        {
            _logger.LogDebug("Enriching cases with legal user data from vwExportToOuterSystems_UserData.");

            // Defensive check: warn if any case has TikNumber but missing/invalid TikCounter
            foreach (var c in cases)
            {
                if (!string.IsNullOrWhiteSpace(c.TikNumber) && c.TikCounter <= 0)
                {
                    _logger.LogWarning(
                        "Case has TikNumber '{TikNumber}' but TikCounter is invalid ({TikCounter}). UserData enrichment may fail. This should not happen from vwExportToOuterSystems_Files.",
                        c.TikNumber, c.TikCounter);
                }
            }

            var tikCounterSet = new HashSet<int>(tikCounters);

            var userDataRows = await _db.UserData
                .AsNoTracking()
                .Where(u => tikCounterSet.Contains(u.TikCounter))
                .ToListAsync(ct);

            static string Norm(string? s)
            {
                if (string.IsNullOrWhiteSpace(s)) return "";
                // Normalize: replace NBSP, trim, collapse spaces
                return string.Join(" ",
                    s.Replace('\u00A0', ' ')
                     .Trim()
                     .Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries));
            }

            var targetPage = Norm(LegalUserDataPageName);

            _logger.LogDebug("LegalUserDataPageName raw='{Raw}', normalized='{Normalized}'", LegalUserDataPageName, targetPage);

            var legalUserDataRows = userDataRows
                .Where(u => Norm(u.PageName) == targetPage)
                .ToList();

            _logger.LogDebug(
                "Loaded {TotalUserData} rows for TikCounters, legal user data rows={LegalUserDataRows}, Distinct pages={Pages}",
                userDataRows.Count,
                legalUserDataRows.Count,
                userDataRows.Select(u => u.PageName).Distinct().ToArray());

            var userDataAllByCase = userDataRows
                .GroupBy(u => u.TikCounter)
                .ToDictionary(g => g.Key, g => g.ToList());

            var userDataByCase = legalUserDataRows
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
                // Always use TikCounter (internal numeric ID), never parse TikNumber
                var tikCounterKey = odcanitCase.TikCounter;

                if (tikCounterKey <= 0)
                {
                    _logger.LogWarning(
                        "Skipping UserData enrichment for case with TikNumber '{TikNumber}' because TikCounter is invalid ({TikCounter}).",
                        odcanitCase.TikNumber, tikCounterKey);
                    continue;
                }

                if (!userDataByCase.TryGetValue(tikCounterKey, out var rows))
                {
                    if (userDataAllByCase.TryGetValue(tikCounterKey, out var allRows))
                    {
                        var availablePages = allRows.Select(r => r.PageName ?? "<null>").Distinct().ToArray();
                        _logger.LogDebug(
                            "No legal user data rows for TikCounter {TikCounter}. Available PageNames: {Pages}",
                            odcanitCase.TikCounter,
                            availablePages);
                    }
                    else
                    {
                        _logger.LogDebug("No user data rows at all for TikCounter {TikCounter}.", odcanitCase.TikCounter);
                    }
                    continue;
                }

                var plaintiffAddressKey = NormalizeUserFieldName("כתובת תובע");
                var defendantNameKey = NormalizeUserFieldName("שם נתבע");
                var defendantAddressKey = NormalizeUserFieldName("כתובת נתבע");

                bool plaintiffAddressFromUserData = false;
                bool defendantNameFromUserData = false;
                bool defendantAddressFromUserData = false;

                foreach (var row in rows)
                {
                    var key = NormalizeUserFieldName(row.FieldName);
                    if (key == null)
                    {
                        continue;
                    }

                    if (plaintiffAddressKey != null && key == plaintiffAddressKey)
                    {
                        plaintiffAddressFromUserData = true;
                    }
                    else if (defendantNameKey != null && key == defendantNameKey)
                    {
                        defendantNameFromUserData = true;
                    }
                    else if (defendantAddressKey != null && key == defendantAddressKey)
                    {
                        defendantAddressFromUserData = true;
                    }

                    if (UserDataFieldHandlers.TryGetValue(key, out var handler))
                    {
                        handler(odcanitCase, row);
                    }
                }

                _logger.LogDebug(
                    "UserData legal fields for TikCounter {TikCounter}: PlaintiffAddressFromUserData={PlaintiffAddressFromUserData}, DefendantNameFromUserData={DefendantNameFromUserData}, DefendantAddressFromUserData={DefendantAddressFromUserData}",
                    odcanitCase.TikCounter,
                    plaintiffAddressFromUserData,
                    defendantNameFromUserData,
                    defendantAddressFromUserData);

                _logger.LogDebug(
                    "Court fields for TikCounter {TikCounter}: CourtCaseNumber='{CourtCaseNumber}', CourtCity='{CourtCity}', CourtName='{CourtName}'",
                    odcanitCase.TikCounter,
                    odcanitCase.CourtCaseNumber ?? "<null>",
                    odcanitCase.CourtCity ?? "<null>",
                    odcanitCase.CourtName ?? "<null>");
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
            Add("סכום נזק ישיר", (c, row) => c.DirectDamageAmount = ExtractDecimal(row) ?? c.DirectDamageAmount);
            Add("סכום פסק דין", (c, row) => c.JudgmentAmount = ExtractDecimal(row) ?? c.JudgmentAmount);
            Add("סכום תביעה מוכח", (c, row) => c.ProvenClaimAmount = ExtractDecimal(row) ?? c.ProvenClaimAmount);
            Add("אגרת בית משפט ( I+II )", (c, row) => c.CourtFeeTotal = ExtractDecimal(row) ?? c.CourtFeeTotal);
            Add("Court fee", (c, row) => c.CourtFeeTotal = ExtractDecimal(row) ?? c.CourtFeeTotal);
            Add("Court fee (I+II)", (c, row) => c.CourtFeeTotal = ExtractDecimal(row) ?? c.CourtFeeTotal);
            Add("מ.אגרה I", (c, row) => c.CourtFeePartOne = ExtractDecimal(row) ?? c.CourtFeePartOne);
            Add("שם עד", (c, row) => c.WitnessName = row.strData);
            Add("סלולרי עד", (c, row) => c.DriverPhone = row.strData);
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
            Add("צד תובע", (c, row) => c.PlaintiffSideRaw = row.strData);
            Add("צד נתבע", (c, row) => c.DefendantSideRaw = row.strData);
            Add("מרחוב (תביעה)", (c, row) => c.ClaimStreet = row.strData);
            Add("כתובת נתבע", (c, row) => c.DefenseStreet = row.strData);
            Add("מרחוב (הגנה)", (c, row) =>
            {
                if (string.IsNullOrWhiteSpace(c.DefenseStreet))
                {
                    c.DefenseStreet = row.strData;
                }
            });
            Add("Event date", (c, row) => c.EventDate = ExtractDate(row) ?? c.EventDate);
            Add("תאריך אירוע", (c, row) => c.EventDate = ExtractDate(row) ?? c.EventDate);
            Add("מועד קבלת כתב התביעה", (c, row) => c.ComplaintReceivedDate = row.dateData ?? c.ComplaintReceivedDate);
            Add("folderID", (c, row) => c.CaseFolderId = row.strData);
            Add("שם עורך דין", (c, row) => c.AttorneyName = row.strData);
            Add("פקס", (c, row) => c.DefendantFax = row.strData);
            Add("שווי שרידים", (c, row) => c.ResidualValueAmount = ExtractDecimal(row) ?? c.ResidualValueAmount);

            // Court fields from UserData
            Add("מספר הליך בית משפט", (c, row) =>
            {
                var raw = row.strData;
                var normalizedCaseNumber = NormalizeCourtCaseNumberAndCity(raw, out var derivedCity);
                if (!string.IsNullOrWhiteSpace(normalizedCaseNumber))
                {
                    c.CourtCaseNumber = normalizedCaseNumber;
                }

                if (!string.IsNullOrWhiteSpace(derivedCity) && string.IsNullOrWhiteSpace(c.CourtCity))
                {
                    c.CourtCity = derivedCity;
                }
            });

            Add("שם בית משפט", (c, row) =>
            {
                var name = row.strData?.Trim();
                if (!string.IsNullOrWhiteSpace(name))
                {
                    c.CourtName = name;
                    var cityFromName = DeriveCourtCityFromCourtName(name);
                    if (!string.IsNullOrWhiteSpace(cityFromName) && string.IsNullOrWhiteSpace(c.CourtCity))
                    {
                        c.CourtCity = cityFromName;
                    }
                }
            });

            // Accident short circumstances: populate Notes from UserData "גרסאות תביעה"
            Add("גרסאות תביעה", (c, row) => c.Notes = row.strData);

            return dict;
        }

        private static string? NormalizeCourtCaseNumberAndCity(string? raw, out string? derivedCity)
        {
            derivedCity = null;

            if (string.IsNullOrWhiteSpace(raw))
            {
                return null;
            }

            var value = raw.Trim();

            // Case number pattern: 1-6 digits - 2 digits - 4 digits (e.g., 2222-02-2025)
            var match = System.Text.RegularExpressions.Regex.Match(value, @"\b(\d{1,6}-\d{2}-\d{4})\b");
            if (!match.Success)
            {
                return string.IsNullOrWhiteSpace(value) ? null : value;
            }

            var caseNumber = match.Groups[1].Value;

            // Anything after the matched case number is treated as potential city (with safeguards)
            var after = value.Substring(match.Index + match.Length).Trim();
            if (!string.IsNullOrWhiteSpace(after))
            {
                // If trailing text looks like a court name (contains "בית משפט"), do NOT treat as city
                if (!after.Contains("בית משפט", StringComparison.Ordinal))
                {
                    derivedCity = after;
                }
            }

            return caseNumber;
        }

        private static string? DeriveCourtCityFromCourtName(string? courtName)
        {
            if (string.IsNullOrWhiteSpace(courtName))
            {
                return null;
            }

            var value = courtName.Trim();

            // Prefer last occurrence of " ב" (space + bet)
            var idx = value.LastIndexOf(" ב", StringComparison.Ordinal);
            if (idx >= 0)
            {
                idx += 2; // skip the space and the 'ב'
            }
            else
            {
                // Fallback: last 'ב' anywhere
                idx = value.LastIndexOf('ב');
                if (idx >= 0)
                {
                    idx += 1;
                }
            }

            if (idx < 0 || idx >= value.Length - 1)
            {
                return null;
            }

            var city = value.Substring(idx).Trim().Trim(',', '.', ' ');
            return string.IsNullOrWhiteSpace(city) ? null : city;
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

