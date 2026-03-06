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
    /// Runs only when HearingBackfill:Enable=true. Creates items with status index=1.
    /// </summary>
    public class HearingBackfillService
    {
        private readonly IntegrationDbContext _db;
        private readonly IMondayClient _mondayClient;
        private readonly IMondayMetadataProvider _metadataProvider;
        private readonly HearingBackfillSettings _settings;
        private readonly ILogger<HearingBackfillService> _logger;

        private const string DateColumnId = "date_mkwjwmzq";
        private const string HourColumnId = "hour_mkwjbwr";
        private const string JudgeColumnId = "text_mkwjne8v";
        private const string CourtCityColumnId = "text_mkxez28d";
        private const string DriverPhoneColumnId = "phone_mkwj7fak";
        private const string TikNumberColumnId = "text_mkwe19hn";
        private const string ClientNumberColumnId = "dropdown_mkxjrssr";
        private const string EventDateColumnId = "date_mkwj3780";

        public HearingBackfillService(
            IntegrationDbContext db,
            IMondayClient mondayClient,
            IMondayMetadataProvider metadataProvider,
            IOptions<HearingBackfillSettings> settings,
            ILogger<HearingBackfillService> logger)
        {
            _db = db;
            _mondayClient = mondayClient;
            _metadataProvider = metadataProvider;
            _settings = settings.Value;
            _logger = logger;
        }

        public async Task<HearingBackfillResult> RunAsync(CancellationToken ct = default)
        {
            var result = new HearingBackfillResult();
            var sw = Stopwatch.StartNew();

            var pending = await _db.HearingBackfillApr2026
                .Where(r => r.ImportStatus == "Pending")
                .OrderBy(r => r.HearingDate)
                .ThenBy(r => r.HearingTime)
                .ThenBy(r => r.TikNumber)
                .Take(_settings.BatchSize)
                .ToListAsync(ct);

            result.TotalPendingScanned = pending.Count;
            if (pending.Count == 0)
            {
                sw.Stop();
                _logger.LogInformation("HEARING BACKFILL | No pending rows. Duration={DurationMs}ms", sw.ElapsedMilliseconds);
                return result;
            }

            var boardId = _settings.BoardId;
            var groupIds = await _mondayClient.GetBoardGroupIdsAsync(boardId, ct);
            var groupId = MondayGroupResolver.ResolveGroupId(
                boardId, groupIds, null, "HearingBackfill:default", _logger);

            HashSet<string>? dropdownLabels = null;
            try
            {
                dropdownLabels = await _metadataProvider.GetAllowedDropdownLabelsAsync(boardId, ClientNumberColumnId, ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "HEARING BACKFILL | Could not fetch dropdown labels for {ColumnId}. ClientNumber column will be omitted for all rows.", ClientNumberColumnId);
            }

            var existingTikNumbers = new HashSet<string>(StringComparer.Ordinal);
            if (pending.Count > 0)
            {
                var tikNumbers = pending.Select(r => r.TikNumber).Where(t => !string.IsNullOrWhiteSpace(t)).Distinct().ToList();
                if (tikNumbers.Count > 0)
                {
                    var alreadyMapped = await _db.MondayItemMappings
                        .AsNoTracking()
                        .Where(m => m.BoardId == boardId && tikNumbers.Contains(m.TikNumber ?? ""))
                        .Select(m => m.TikNumber)
                        .ToListAsync(ct);
                    foreach (var t in alreadyMapped.Where(t => !string.IsNullOrEmpty(t)))
                        existingTikNumbers.Add(t!);
                }
            }

            foreach (var row in pending)
            {
                ct.ThrowIfCancellationRequested();

                if (row.ImportStatus == "Imported")
                {
                    result.Skipped++;
                    continue;
                }

                if (!string.IsNullOrWhiteSpace(row.TikNumber) && existingTikNumbers.Contains(row.TikNumber))
                {
                    result.Skipped++;
                    _logger.LogInformation("HEARING BACKFILL | Skipped (already mapped) TikNumber={TikNumber}", row.TikNumber);
                    continue;
                }

                try
                {
                    var columnValues = BuildColumnValues(row, dropdownLabels, out var dropdownSkipped);
                    if (dropdownSkipped)
                        result.DropdownSkipped++;

                    var itemName = $"דיון {row.TikNumber ?? row.Id.ToString()}";
                    var columnValuesJson = JsonSerializer.Serialize(columnValues);

                    var mondayItemId = await _mondayClient.CreateItemAsync(boardId, groupId, itemName, columnValuesJson, ct);

                    row.ImportStatus = "Imported";
                    row.ImportedAtUtc = DateTime.UtcNow;
                    row.MondayItemId = mondayItemId;
                    row.ImportError = null;
                    await _db.SaveChangesAsync(ct);

                    result.Imported++;
                    _logger.LogInformation(
                        "HEARING BACKFILL | Imported TikNumber={TikNumber}, ClientNumber={ClientNumber}, MondayItemId={MondayItemId}, status index=1",
                        row.TikNumber, row.ClientNumber, mondayItemId);
                }
                catch (Exception ex)
                {
                    result.Failed++;
                    var errMsg = ex.Message.Length > 3990 ? ex.Message[..3990] + "..." : ex.Message;
                    row.ImportStatus = "Failed";
                    row.ImportError = errMsg;
                    row.FailedAtUtc = DateTime.UtcNow;
                    await _db.SaveChangesAsync(ct);
                    _logger.LogError(ex, "HEARING BACKFILL | Failed TikNumber={TikNumber}, ClientNumber={ClientNumber}", row.TikNumber, row.ClientNumber);
                }
            }

            sw.Stop();
            result.DurationMs = sw.ElapsedMilliseconds;
            _logger.LogInformation(
                "HEARING BACKFILL SUMMARY | PendingScanned={Pending}, Imported={Imported}, Failed={Failed}, Skipped={Skipped}, DropdownSkipped={DropdownSkipped}, BatchSize={BatchSize}, DurationMs={DurationMs}",
                result.TotalPendingScanned, result.Imported, result.Failed, result.Skipped, result.DropdownSkipped, _settings.BatchSize, result.DurationMs);

            return result;
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

            if (!string.IsNullOrWhiteSpace(row.CourtName))
                cv[CourtCityColumnId] = row.CourtName.Trim();

            if (!string.IsNullOrWhiteSpace(row.DriverPhone))
            {
                var normalized = NormalizePhone(row.DriverPhone);
                if (!string.IsNullOrEmpty(normalized))
                    cv[DriverPhoneColumnId] = new { phone = normalized, countryShortName = "IL" };
            }

            if (!string.IsNullOrWhiteSpace(row.TikNumber))
                cv[TikNumberColumnId] = row.TikNumber.Trim();

            if (!string.IsNullOrWhiteSpace(row.ClientNumber) && dropdownLabels != null)
            {
                var val = row.ClientNumber.Trim();
                if (dropdownLabels.Contains(val))
                    cv[ClientNumberColumnId] = new { labels = new[] { val } };
                else
                {
                    dropdownSkipped = true;
                    _logger.LogWarning(
                        "HEARING BACKFILL | Dropdown label not found, skipping column | ColumnId={ColumnId}, MissingValue={Value}, TikNumber={TikNumber}",
                        ClientNumberColumnId, val, row.TikNumber);
                }
            }

            if (row.EventDate.HasValue)
                cv[EventDateColumnId] = new { date = row.EventDate.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) };

            cv[_settings.StatusColumnId] = new { index = _settings.ImportedStatusIndex };

            return cv;
        }

        private static string? NormalizePhone(string? phone)
        {
            if (string.IsNullOrWhiteSpace(phone)) return null;
            var digits = new string(phone.Where(char.IsDigit).ToArray());
            if (digits.Length == 0) return null;
            if (digits.StartsWith("972") && digits.Length >= 12)
                return "+" + digits;
            if (digits.Length == 9 && digits.StartsWith("0") == false)
                return "+972" + digits;
            if (digits.Length == 10 && digits.StartsWith("0"))
                return "+972" + digits[1..];
            return "+972" + digits;
        }
    }

    public class HearingBackfillResult
    {
        public int TotalPendingScanned { get; set; }
        public int Imported { get; set; }
        public int Failed { get; set; }
        public int Skipped { get; set; }
        public int DropdownSkipped { get; set; }
        public long DurationMs { get; set; }
    }
}
