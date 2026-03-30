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

namespace Odmon.Worker.Services
{
    /// <summary>
    /// One-time April 2026 hearings backfill from IntegrationDb into Monday board 5035534500.
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

        private const string StatusLabel = "תיק נמצא באמצע תהליך";

        private const string DateColumnId = "date_mkwjwmzq";
        private const string HourColumnId = "hour_mkwjbwr";
        private const string JudgeColumnId = "text_mkwjne8v";
        // CourtCityColumnId removed — text_mkxez28d is now populated from legal UserData only via SyncService.
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
            MondayMappingReadService mappingReader)
        {
            _db = db;
            _mondayClient = mondayClient;
            _metadataProvider = metadataProvider;
            _settings = settings.Value;
            _logger = logger;
            _mappingReader = mappingReader;
        }

        public async Task<HearingBackfillResult> RunAsync(CancellationToken ct = default)
        {
            var result = new HearingBackfillResult();
            var sw = Stopwatch.StartNew();
            var boardId = _settings.BoardId;

            // Use raw SQL for OFFSET/FETCH since we have no Id
            var rawSql = @"
SELECT [תאריך דיון], [שעת דיון], [שם שופט], [שם ביהמש], [טלפון נהג], [שם נהג], [מספר תיק], [מספר לקוח], [תאריך אירוע]
FROM [dbo].[HearingBackfill_Apr2026]
ORDER BY [תאריך דיון], [שעת דיון], [מספר תיק]
OFFSET {0} ROWS FETCH NEXT {1} ROWS ONLY";

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

                var tikNumbers = batch.Select(r => r.TikNumber).Where(t => !string.IsNullOrWhiteSpace(t)).Distinct().ToList();
                var alreadyMapped = new HashSet<string>(StringComparer.Ordinal);
                if (tikNumbers.Count > 0)
                {
                    var mapped = await _mappingReader.GetMappedTikNumbersAsync(boardId, tikNumbers, ct);
                    foreach (var t in mapped)
                        alreadyMapped.Add(t);
                }

                var nextTikCounterForBackfill = await GetNextBackfillTikCounterAsync(boardId, ct);

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
                        var columnValues = BuildColumnValues(row, dropdownLabels, out var dropdownSkipped);
                        if (dropdownSkipped)
                            result.DropdownSkipped++;

                        var itemName = $"דיון {row.TikNumber ?? "?"}";
                        var columnValuesJson = JsonSerializer.Serialize(columnValues);

                        var mondayItemId = await _mondayClient.CreateItemAsync(boardId, groupId, itemName, columnValuesJson, ct);

                        created++;
                        _logger.LogInformation("HEARING BACKFILL | Created TikNumber={TikNumber}, MondayItemId={MondayItemId}, status={Status}", row.TikNumber, mondayItemId, StatusLabel);

                        await AddMappingAsync(boardId, mondayItemId, row.TikNumber, nextTikCounterForBackfill--, ct);
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

        private async Task<int> GetNextBackfillTikCounterAsync(long boardId, CancellationToken ct)
        {
            var min = await _mappingReader.GetMinNegativeTikCounterAsync(boardId, ct);
            return (min ?? 0) - 1;
        }

        private async Task AddMappingAsync(long boardId, long mondayItemId, string? tikNumber, int tikCounter, CancellationToken ct)
        {
            try
            {
                var mapping = new MondayItemMapping
                {
                    TikCounter = tikCounter,
                    TikNumber = tikNumber,
                    MondayItemId = mondayItemId,
                    BoardId = boardId,
                    CreatedAtUtc = DateTime.UtcNow
                };
                _db.MondayItemMappings.Add(mapping);
                await _db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException ex)
            {
                _logger.LogWarning(ex, "HEARING BACKFILL | Could not add MondayItemMapping for TikNumber={TikNumber}, MondayItemId={MondayItemId}. Dedup may fail on next run.", tikNumber, mondayItemId);
            }
        }

        private Dictionary<string, object> BuildColumnValues(HearingBackfillApr2026 row, HashSet<string>? dropdownLabels, out bool dropdownSkipped)
        {
            dropdownSkipped = false;
            var cv = new Dictionary<string, object>();

            if (row.HearingDate.HasValue)
                cv[DateColumnId] = new { date = row.HearingDate.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) };

            if (row.HearingTime.HasValue)
            {
                var h = row.HearingTime.Value.Hours;
                var m = row.HearingTime.Value.Minutes;
                if (h >= 0 && h <= 23 && m >= 0 && m <= 59)
                    cv[HourColumnId] = new { hour = h, minute = m };
            }

            if (!string.IsNullOrWhiteSpace(row.JudgeName))
                cv[JudgeColumnId] = row.JudgeName.Trim();

            // text_mkxez28d is now populated from legal UserData "שם בית משפט" only (via SyncService), not from hearing events.

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

            if (row.ClientNumber.HasValue && dropdownLabels != null)
            {
                var clientNumberText = row.ClientNumber.Value.ToString();
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
