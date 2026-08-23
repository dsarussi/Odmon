using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Odmon.Worker.Configuration;
using Odmon.Worker.Data;
using Odmon.Worker.Monday;
using Odmon.Worker.Models;
using Odmon.Worker.OdcanitAccess;

namespace Odmon.Worker.Services
{
    /// <summary>
    /// Hearings backfill from IntegrationDb (HearingBackfill:SourceTable, same shape as April 2026) into Monday.
    /// Runs only when HearingBackfill:Enable=true. No tracking columns; dedup via MondayItemMappings.
    /// </summary>
    public class HearingBackfillService
    {
        private readonly IntegrationDbContext _db;
        private readonly IMondayClient _mondayClient;
        private readonly IMondayMetadataProvider _metadataProvider;
        private readonly HearingBackfillSettings _settings;
        private readonly ILogger<HearingBackfillService> _logger;
        private readonly MondayMappingReadService _mappingReader;
        private readonly IOdcanitReader _odcanitReader;

        private const string StatusLabel = "באמצע תהליך";

        private const string DateColumnId = "date_mkwjwmzq";
        private const string HourColumnId = "hour_mkwjbwr";
        private const string JudgeColumnId = "text_mkwjne8v";
        private const string CourtCityColumnId = "text_mkxez28d";
        private const string DriverPhoneColumnId = "phone_mkwj7fak";
        private const string DriverNameColumnId = "text_mkwja7cv";
        private const string TikNumberColumnId = "text_mkwe19hn";
        private const string ClientNumberColumnId = "dropdown_mkxjrssr";
        private const string EventDateColumnId = "date_mkwj3780";
        public HearingBackfillService(
            IntegrationDbContext db,
            IMondayClient mondayClient,
            IMondayMetadataProvider metadataProvider,
            IOptions<HearingBackfillSettings> settings,
            ILogger<HearingBackfillService> logger,
            MondayMappingReadService mappingReader,
            IOdcanitReader odcanitReader)
        {
            _db = db;
            _mondayClient = mondayClient;
            _metadataProvider = metadataProvider;
            _settings = settings.Value;
            _logger = logger;
            _mappingReader = mappingReader;
            _odcanitReader = odcanitReader;
        }

        public async Task<HearingBackfillResult> RunAsync(CancellationToken ct = default)
        {
            var result = new HearingBackfillResult();
            var sw = Stopwatch.StartNew();
            var boardId = _settings.BoardId;
            var fromQualified = HearingBackfillSettings.BuildBracketedQualifiedTable(_settings.SourceTable);

            _logger.LogInformation(
                "HEARING BACKFILL | Run starting | SourceTable={SourceTable}, BoardId={BoardId}, BatchSize={BatchSize}",
                _settings.SourceTable, boardId, _settings.BatchSize);

            // Use raw SQL for OFFSET/FETCH since we have no Id (table from config; identifiers validated)
            // All polymorphic columns projected as nvarchar so EF reads string properties without type-cast failures.
            var rawSql = $@"
SELECT [תאריך דיון], CONVERT(NVARCHAR(32), [שעת דיון]) AS [שעת דיון], [שם שופט], [עיר בית משפט], [טלפון נהג], [שם נהג], [מספר תיק], CONVERT(NVARCHAR(64), [מספר לקוח]) AS [מספר לקוח], [תאריך אירוע]
FROM {fromQualified}
ORDER BY [תאריך דיון], [שעת דיון], [מספר תיק]
OFFSET {{0}} ROWS FETCH NEXT {{1}} ROWS ONLY";

            var batchSize = _settings.BatchSize;
            var offset = 0;
            var processed = 0;
            var created = 0;
            var skipped = 0;
            var failed = 0;

            while (true)
            {
                ct.ThrowIfCancellationRequested();

                var batch = await _db.HearingBackfillApr2026
                    .FromSqlRaw(rawSql, offset, batchSize)
                    .AsNoTracking()
                    .ToListAsync(ct);

                if (batch.Count == 0)
                {
                    result.NoMoreRows = true;
                    _logger.LogInformation("HEARING BACKFILL | No more rows. Processed={Processed}, Created={Created}, Skipped={Skipped}, Failed={Failed}", processed, created, skipped, failed);
                    break;
                }

                var groupIds = await _mondayClient.GetBoardGroupIdsAsync(boardId, ct);
                var groupId = MondayGroupResolver.ResolveGroupId(boardId, groupIds, null, "HearingBackfill:default", _logger);

                HashSet<string>? dropdownLabels = null;
                try
                {
                    dropdownLabels = await _metadataProvider.GetAllowedDropdownLabelsAsync(boardId, ClientNumberColumnId, ct);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "HEARING BACKFILL | Could not fetch dropdown labels for {ColumnId}. ClientNumber column will be omitted for all rows.", ClientNumberColumnId);
                }

                var tikNumbers = batch
                    .Select(r => r.TikNumber)
                    .Where(t => !string.IsNullOrWhiteSpace(t))
                    .Select(t => t!)
                    .Distinct()
                    .ToList();
                var alreadyMapped = new HashSet<string>(StringComparer.Ordinal);
                if (tikNumbers.Count > 0)
                {
                    var mapped = await _mappingReader.GetMappedTikNumbersAsync(boardId, tikNumbers, ct);
                    foreach (var t in mapped)
                        alreadyMapped.Add(t);
                }

                var resolvedTikCounters = await _odcanitReader.ResolveTikNumbersToCountersAsync(tikNumbers, ct);

                foreach (var row in batch)
                {
                    ct.ThrowIfCancellationRequested();
                    processed++;

                    _logger.LogDebug("HEARING BACKFILL | Processing TikNumber={TikNumber}", row.TikNumber);

                    if (!string.IsNullOrWhiteSpace(row.TikNumber) && alreadyMapped.Contains(row.TikNumber))
                    {
                        skipped++;
                        _logger.LogInformation("HEARING BACKFILL | Skipped (already mapped) TikNumber={TikNumber}", row.TikNumber);
                        continue;
                    }

                    try
                    {
                        if (string.IsNullOrWhiteSpace(row.TikNumber) ||
                            !resolvedTikCounters.TryGetValue(row.TikNumber.Trim(), out var realTikCounter) ||
                            realTikCounter <= 0)
                        {
                            failed++;
                            _logger.LogCritical(
                                "HEARING BACKFILL | Mapping integrity failure: cannot resolve real Odcanit TikCounter for TikNumber={TikNumber}. Monday item will not be created.",
                                row.TikNumber ?? "<null>");
                            continue;
                        }

                        var columnValues = BuildColumnValues(row, dropdownLabels, out var dropdownSkipped);
                        if (dropdownSkipped)
                            result.DropdownSkipped++;

                        var itemName = $"דיון {row.TikNumber ?? "?"}";
                        var columnValuesJson = JsonSerializer.Serialize(columnValues);

                        var mondayItemId = await _mondayClient.CreateItemAsync(boardId, groupId, itemName, columnValuesJson, ct);

                        created++;
                        _logger.LogInformation("HEARING BACKFILL | Created TikNumber={TikNumber}, MondayItemId={MondayItemId}, status={Status}", row.TikNumber, mondayItemId, StatusLabel);

                        await AddMappingAsync(boardId, mondayItemId, row.TikNumber, realTikCounter, ct);
                        alreadyMapped.Add(row.TikNumber ?? "");
                    }
                    catch (Exception ex)
                    {
                        failed++;
                        _logger.LogError(ex, "HEARING BACKFILL | Failed TikNumber={TikNumber}, ClientNumber={ClientNumber}", row.TikNumber, row.ClientNumber);
                    }
                }

                offset += batchSize;
                if (batch.Count < batchSize)
                {
                    result.NoMoreRows = true;
                    break;
                }
            }

            sw.Stop();
            result.Processed = processed;
            result.Created = created;
            result.Skipped = skipped;
            result.Failed = failed;
            result.DurationMs = sw.ElapsedMilliseconds;

            _logger.LogInformation(
                "HEARING BACKFILL SUMMARY | Processed={Processed}, Created={Created}, Skipped={Skipped}, Failed={Failed}, DropdownSkipped={DropdownSkipped}, BatchSize={BatchSize}, DurationMs={DurationMs}",
                processed, created, skipped, failed, result.DropdownSkipped, batchSize, result.DurationMs);

            return result;
        }

        private async Task AddMappingAsync(long boardId, long mondayItemId, string? tikNumber, int tikCounter, CancellationToken ct)
        {
            try
            {
                var mapping = CreateValidatedMapping(boardId, mondayItemId, tikNumber, tikCounter, DateTime.UtcNow);
                _db.MondayItemMappings.Add(mapping);
                await _db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException ex)
            {
                _logger.LogWarning(ex, "HEARING BACKFILL | Could not add MondayItemMapping for TikNumber={TikNumber}, MondayItemId={MondayItemId}. Dedup may fail on next run.", tikNumber, mondayItemId);
            }
        }

        internal static MondayItemMapping CreateValidatedMapping(
            long boardId,
            long mondayItemId,
            string? tikNumber,
            int tikCounter,
            DateTime createdAtUtc)
        {
            var mapping = new MondayItemMapping
            {
                TikCounter = tikCounter,
                TikNumber = tikNumber,
                MondayItemId = mondayItemId,
                BoardId = boardId,
                CreatedAtUtc = createdAtUtc
            };
            MondayItemMappingIntegrityService.ValidateNewMapping(mapping, "HearingBackfill");
            return mapping;
        }

        private Dictionary<string, object> BuildColumnValues(HearingBackfillApr2026 row, HashSet<string>? dropdownLabels, out bool dropdownSkipped)
        {
            dropdownSkipped = false;
            var cv = new Dictionary<string, object>();

            if (row.HearingDate.HasValue)
                cv[DateColumnId] = new { date = row.HearingDate.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) };

            if (!string.IsNullOrWhiteSpace(row.HearingTime) && TryParseHourMinute(row.HearingTime, out var h, out var m))
            {
                if (h >= 0 && h <= 23 && m >= 0 && m <= 59)
                    cv[HourColumnId] = new { hour = h, minute = m };
            }

            if (!string.IsNullOrWhiteSpace(row.JudgeName))
                cv[JudgeColumnId] = row.JudgeName.Trim();

            if (!string.IsNullOrWhiteSpace(row.CourtCity))
                cv[CourtCityColumnId] = row.CourtCity.Trim();

            if (!string.IsNullOrWhiteSpace(row.DriverPhone))
            {
                var normalized = NormalizePhone(row.DriverPhone);
                if (!string.IsNullOrEmpty(normalized))
                    cv[DriverPhoneColumnId] = new { phone = normalized, countryShortName = "IL" };
            }

            if (!string.IsNullOrWhiteSpace(row.DriverName))
                cv[DriverNameColumnId] = row.DriverName.Trim();

            if (!string.IsNullOrWhiteSpace(row.TikNumber))
                cv[TikNumberColumnId] = row.TikNumber.Trim();

            if (!string.IsNullOrWhiteSpace(row.ClientNumber) && dropdownLabels != null)
            {
                var clientNumberText = row.ClientNumber.Trim();
                if (dropdownLabels.Contains(clientNumberText))
                    cv[ClientNumberColumnId] = new { labels = new[] { clientNumberText } };
                else
                {
                    dropdownSkipped = true;
                    _logger.LogWarning(
                        "HEARING BACKFILL | Dropdown label not found, skipping column | ColumnId={ColumnId}, MissingValue={Value}, TikNumber={TikNumber}",
                        ClientNumberColumnId, clientNumberText, row.TikNumber);
                }
            }

            if (row.EventDate.HasValue)
                cv[EventDateColumnId] = new { date = row.EventDate.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) };

            cv[_settings.StatusColumnId] = new { label = StatusLabel };

            return cv;
        }

        /// <summary>
        /// Parses "HH:MM" or "HH:MM:SS" (from CONVERT(NVARCHAR, time) or raw string column).
        /// </summary>
        private static bool TryParseHourMinute(string raw, out int hour, out int minute)
        {
            hour = 0;
            minute = 0;
            if (TimeSpan.TryParse(raw.Trim(), out var ts))
            {
                hour = ts.Hours;
                minute = ts.Minutes;
                return true;
            }
            return false;
        }

        private static string? NormalizePhone(string? phone)
        {
            if (string.IsNullOrWhiteSpace(phone)) return null;
            var digits = new string(phone.Where(char.IsDigit).ToArray());
            if (digits.Length == 0) return null;
            if (digits.StartsWith("972") && digits.Length >= 12)
                return "+" + digits;
            if (digits.Length == 9 && !digits.StartsWith("0"))
                return "+972" + digits;
            if (digits.Length == 10 && digits.StartsWith("0"))
                return "+972" + digits[1..];
            return "+972" + digits;
        }
    }

    public class HearingBackfillResult
    {
        public int Processed { get; set; }
        public int Created { get; set; }
        public int Skipped { get; set; }
        public int Failed { get; set; }
        public int DropdownSkipped { get; set; }
        public long DurationMs { get; set; }
        /// <summary>True when no more rows in source table (empty batch).</summary>
        public bool NoMoreRows { get; set; }
    }
}
