using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Odmon.Worker.Data;
using Odmon.Worker.Monday;
using Odmon.Worker.Models;
using Odmon.Worker.OdcanitAccess;

namespace Odmon.Worker.Services
{
    /// <summary>
    /// Syncs the nearest upcoming hearing per TikCounter from vwExportToOuterSystems_YomanData to Monday.
    /// Applies correct update ordering so WhatsApp triggers show "rescheduled" vs "new hearing".
    /// </summary>
    public class HearingNearestSyncService
    {
        private static readonly TimeZoneInfo IsraelTimeZone = TimeZoneInfo.FindSystemTimeZoneById("Israel Standard Time");

        /// <summary>MeetStatus: 0=active, 1=cancelled, 2=rescheduled.</summary>
        private static readonly IReadOnlyDictionary<int, string> MeetStatusToLabel = new Dictionary<int, string>
        {
            [0] = "פעיל",
            [1] = "מבוטל",
            [2] = "הועבר"
        };

        private readonly IOdcanitReader _odcanitReader;
        private readonly IntegrationDbContext _integrationDb;
        private readonly IMondayClient _mondayClient;
        private readonly IMondayMetadataProvider _mondayMetadataProvider;
        private readonly IConfiguration _config;
        private readonly MondaySettings _mondaySettings;
        private readonly ILogger<HearingNearestSyncService> _logger;
        private readonly ISkipLogger _skipLogger;

        public HearingNearestSyncService(
            IOdcanitReader odcanitReader,
            IntegrationDbContext integrationDb,
            IMondayClient mondayClient,
            IMondayMetadataProvider mondayMetadataProvider,
            IConfiguration config,
            IOptions<MondaySettings> mondayOptions,
            ILogger<HearingNearestSyncService> logger,
            ISkipLogger skipLogger)
        {
            _odcanitReader = odcanitReader;
            _integrationDb = integrationDb;
            _mondayClient = mondayClient;
            _mondayMetadataProvider = mondayMetadataProvider;
            _config = config;
            _mondaySettings = mondayOptions.Value ?? new MondaySettings();
            _logger = logger;
            _skipLogger = skipLogger;
        }

        /// <summary>
        /// Syncs nearest upcoming hearing per TikCounter to Monday. Uses OdcanitWrites:Enable and DryRun.
        /// </summary>
        public async Task SyncNearestHearingsAsync(long boardId, CancellationToken ct)
        {
            var testingEnabled = _config.GetValue<bool>("Testing:Enable", false);
            if (testingEnabled)
            {
                _logger.LogInformation(
                    "Testing.Enable=true - skipping HearingNearestSyncService (no Odcanit access). BoardId={BoardId}",
                    boardId);
                return;
            }

            var enableWrites = _config.GetValue<bool>("OdcanitWrites:Enable", false);
            var dryRun = _config.GetValue<bool>("OdcanitWrites:DryRun", true);
            var mode = !enableWrites ? "disabled" : (dryRun ? "dryrun" : "live");

            _logger.LogInformation(
                "HearingNearest sync: Mode={Mode}, BoardId={BoardId}, Enable={Enable}, DryRun={DryRun}",
                mode, boardId, enableWrites, dryRun);

            var mappings = await _integrationDb.MondayItemMappings
                .AsNoTracking()
                .Where(m => m.BoardId == boardId)
                .ToListAsync(ct);

            if (mappings.Count == 0)
            {
                _logger.LogDebug("No Monday mappings for board {BoardId}; skipping hearing sync.", boardId);
                return;
            }

            var tikCounters = mappings.Select(m => m.TikCounter).Distinct().ToList();
            var diaryRows = await _odcanitReader.GetDiaryEventsByTikCountersAsync(tikCounters, ct);
            var nowLocal = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, IsraelTimeZone);
            var nearestByTik = HearingSelector.PickNearestUpcomingHearing(diaryRows, nowLocal);

            var tableExists = await TableExistsAsync(_integrationDb, "HearingNearestSnapshots", ct);
            if (!tableExists)
            {
                _logger.LogWarning(
                    "Table HearingNearestSnapshots does not exist; skipping HearingNearest sync. BoardId={BoardId}",
                    boardId);
                return;
            }

            var statusColumnId = _mondaySettings.HearingStatusColumnId;
            HashSet<string>? allowedStatusLabels = null;
            if (!string.IsNullOrWhiteSpace(statusColumnId))
            {
                allowedStatusLabels = await _mondayMetadataProvider.GetAllowedDropdownLabelsAsync(boardId, statusColumnId, ct);
                if (allowedStatusLabels.Count == 0)
                    _logger.LogWarning("Hearing status column {ColumnId} on board {BoardId} has no labels; status updates will be skipped.", statusColumnId, boardId);
            }

            foreach (var mapping in mappings)
            {
                if (!nearestByTik.TryGetValue(mapping.TikCounter, out var hearing))
                {
                    continue;
                }

                if (!RequiredFieldsPresent(hearing))
                {
                    _logger.LogWarning(
                        "Hearing sync skipped (missing required fields): TikCounter={TikCounter}, MondayItemId={MondayItemId}, StartDate={StartDate}, JudgeName={JudgeName}, City={City}",
                        mapping.TikCounter, mapping.MondayItemId,
                        hearing.StartDate.HasValue ? hearing.StartDate.Value.ToString("O") : "<null>",
                        string.IsNullOrWhiteSpace(hearing.JudgeName) ? "<null>" : hearing.JudgeName,
                        string.IsNullOrWhiteSpace(hearing.City) ? "<null>" : hearing.City);
                    continue;
                }

                var meetStatus = hearing.MeetStatus ?? 0;
                if (!MeetStatusToLabel.TryGetValue(meetStatus, out var label))
                {
                    _logger.LogWarning("Unknown MeetStatus={MeetStatus} for TikCounter={TikCounter}; skipping.", meetStatus, mapping.TikCounter);
                    continue;
                }

                if (allowedStatusLabels != null && !allowedStatusLabels.Contains(label))
                {
                    _logger.LogWarning(
                        "Hearing status label '{Label}' not found on Monday column {ColumnId} for board {BoardId}; skipping status update for TikCounter={TikCounter}, MondayItemId={MondayItemId}.",
                        label, statusColumnId, boardId, mapping.TikCounter, mapping.MondayItemId);

                    await _skipLogger.LogSkipAsync(
                        mapping.TikCounter,
                        mapping.TikNumber,
                        operation: "MondayColumnValueValidation",
                        reasonCode: "monday_invalid_status_label",
                        entityId: statusColumnId ?? string.Empty,
                        rawValue: label,
                        details: new
                        {
                            EntityType = "Status",
                            BoardId = boardId,
                            MondayItemId = mapping.MondayItemId,
                            AllowedLabelCount = allowedStatusLabels?.Count ?? 0
                        },
                        ct);
                }

                var snapshot = await _integrationDb.HearingNearestSnapshots
                    .FirstOrDefaultAsync(s => s.TikCounter == mapping.TikCounter && s.BoardId == boardId, ct);

                var startDateUtc = hearing.StartDate!.Value.Kind == DateTimeKind.Utc
                    ? hearing.StartDate.Value
                    : TimeZoneInfo.ConvertTimeToUtc(hearing.StartDate.Value, IsraelTimeZone);
                var judgeName = hearing.JudgeName!.Trim();
                var city = hearing.City!.Trim();

                var (plannedSteps, startDateChanged, statusChanged, judgeOrCityChanged) =
                    HearingNearestSyncServiceHelper.ComputePlannedSteps(hearing, snapshot);
                var snapshotStartUtc = snapshot?.NearestStartDateUtc;
                var snapshotStatus = snapshot?.NearestMeetStatus;

                if (plannedSteps.Count == 0)
                {
                    _logger.LogDebug(
                        "Hearing sync no-op (no change): TikCounter={TikCounter}, MondayItemId={MondayItemId}, StartDate={StartDate}, MeetStatus={MeetStatus}",
                        mapping.TikCounter, mapping.MondayItemId, hearing.StartDate!.Value.ToString("yyyy-MM-dd HH:mm"), meetStatus);
                    continue;
                }

                _logger.LogInformation(
                    "Hearing sync planned: TikCounter={TikCounter}, TikNumber={TikNumber}, MondayItemId={MondayItemId}, StartDate={StartDate}, MeetStatus={MeetStatus}, Steps=[{Steps}], SnapshotOld=[StartDate={SnapshotStart}, Status={SnapshotStatus}]",
                    mapping.TikCounter, mapping.TikNumber, mapping.MondayItemId,
                    hearing.StartDate!.Value.ToString("yyyy-MM-dd HH:mm"), meetStatus, string.Join(", ", plannedSteps),
                    snapshotStartUtc?.ToString("yyyy-MM-dd HH:mm") ?? "<null>", snapshotStatus?.ToString() ?? "<null>");

                if (!enableWrites || dryRun)
                {
                    _logger.LogInformation(
                        "Hearing sync dry run: TikCounter={TikCounter}, MondayItemId={MondayItemId}, would execute steps: [{Steps}]",
                        mapping.TikCounter, mapping.MondayItemId, string.Join(", ", plannedSteps));
                    continue;
                }

                var executedSteps = new List<string>();
                try
                {
                    var judgeCol = _mondaySettings.JudgeNameColumnId ?? "";
                    var cityCol = _mondaySettings.CourtCityColumnId ?? "";
                    var dateCol = _mondaySettings.HearingDateColumnId ?? "";
                    var hourCol = _mondaySettings.HearingHourColumnId ?? "";

                    if (meetStatus == 2)
                    {
                        if (statusChanged && !string.IsNullOrWhiteSpace(statusColumnId) && allowedStatusLabels != null && allowedStatusLabels.Contains(label))
                        {
                            await _mondayClient.UpdateHearingStatusAsync(boardId, mapping.MondayItemId, label, statusColumnId, ct);
                            executedSteps.Add("SetStatus_הועבר");
                        }
                        if (judgeOrCityChanged)
                        {
                            await _mondayClient.UpdateHearingDetailsAsync(boardId, mapping.MondayItemId, judgeName, city, judgeCol, cityCol, ct);
                            executedSteps.Add("UpdateJudgeCity");
                        }
                        if (startDateChanged)
                        {
                            await _mondayClient.UpdateHearingDateAsync(boardId, mapping.MondayItemId, hearing.StartDate!.Value, dateCol, hourCol, ct);
                            executedSteps.Add("UpdateHearingDate");
                        }
                    }
                    else if (meetStatus == 0)
                    {
                        if (judgeOrCityChanged)
                        {
                            await _mondayClient.UpdateHearingDetailsAsync(boardId, mapping.MondayItemId, judgeName, city, judgeCol, cityCol, ct);
                            executedSteps.Add("UpdateJudgeCity");
                        }
                        if (startDateChanged)
                        {
                            await _mondayClient.UpdateHearingDateAsync(boardId, mapping.MondayItemId, hearing.StartDate!.Value, dateCol, hourCol, ct);
                            executedSteps.Add("UpdateHearingDate");
                        }
                        if (statusChanged && !string.IsNullOrWhiteSpace(statusColumnId) && allowedStatusLabels != null && allowedStatusLabels.Contains(label))
                        {
                            await _mondayClient.UpdateHearingStatusAsync(boardId, mapping.MondayItemId, label, statusColumnId, ct);
                            executedSteps.Add("SetStatus_פעיל");
                        }
                    }
                    else if (meetStatus == 1)
                    {
                        if (statusChanged && !string.IsNullOrWhiteSpace(statusColumnId) && allowedStatusLabels != null && allowedStatusLabels.Contains(label))
                        {
                            await _mondayClient.UpdateHearingStatusAsync(boardId, mapping.MondayItemId, label, statusColumnId, ct);
                            executedSteps.Add("SetStatus_מבוטל");
                        }
                    }

                    var nowUtc = DateTime.UtcNow;
                    if (snapshot == null)
                    {
                        _integrationDb.HearingNearestSnapshots.Add(new HearingNearestSnapshot
                        {
                            TikCounter = mapping.TikCounter,
                            BoardId = boardId,
                            MondayItemId = mapping.MondayItemId,
                            NearestStartDateUtc = startDateUtc,
                            NearestMeetStatus = meetStatus,
                            JudgeName = judgeName,
                            City = city,
                            LastSyncedAtUtc = nowUtc
                        });
                    }
                    else
                    {
                        snapshot.NearestStartDateUtc = startDateUtc;
                        snapshot.NearestMeetStatus = meetStatus;
                        snapshot.JudgeName = judgeName;
                        snapshot.City = city;
                        snapshot.LastSyncedAtUtc = nowUtc;
                    }

                    await _integrationDb.SaveChangesAsync(ct);

                    _logger.LogInformation(
                        "Hearing sync succeeded: TikCounter={TikCounter}, MondayItemId={MondayItemId}, ExecutedSteps=[{Steps}], SnapshotNew=[StartDate={StartDate}, Status={MeetStatus}]",
                        mapping.TikCounter, mapping.MondayItemId, string.Join(", ", executedSteps), startDateUtc.ToString("yyyy-MM-dd HH:mm"), meetStatus);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex,
                        "Hearing sync failed: TikCounter={TikCounter}, MondayItemId={MondayItemId}, PlannedSteps=[{Planned}], ExecutedSteps=[{Executed}]",
                        mapping.TikCounter, mapping.MondayItemId, string.Join(", ", plannedSteps), string.Join(", ", executedSteps));
                }
            }
        }

        private static bool RequiredFieldsPresent(OdcanitDiaryEvent hearing)
        {
            return hearing.StartDate.HasValue
                   && !string.IsNullOrWhiteSpace(hearing.JudgeName)
                   && !string.IsNullOrWhiteSpace(hearing.City);
        }

        /// <summary>
        /// Checks if a table exists in the database using sys.tables (does not reference the table itself).
        /// </summary>
        private static async Task<bool> TableExistsAsync(IntegrationDbContext db, string tableName, CancellationToken ct)
        {
            var connection = db.Database.GetDbConnection();
            var wasClosed = connection.State == ConnectionState.Closed;
            if (wasClosed)
            {
                await connection.OpenAsync(ct);
            }

            try
            {
                await using var command = connection.CreateCommand();
                command.CommandText = "SELECT 1 FROM sys.tables WHERE name = @name";
                var p = command.CreateParameter();
                p.ParameterName = "@name";
                p.Value = tableName;
                command.Parameters.Add(p);

                var result = await command.ExecuteScalarAsync(ct);
                return result != null && result != DBNull.Value;
            }
            finally
            {
                // Connection is owned by the context; do not close it
            }
        }
    }
}
