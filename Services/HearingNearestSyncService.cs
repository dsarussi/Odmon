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

            // Load ListenerState T0 to filter out pre-T0 mappings
            var listenerState = await _integrationDb.ListenerStates
                .AsNoTracking()
                .FirstOrDefaultAsync(x => x.Id == 1, ct);

            var allMappings = await _integrationDb.MondayItemMappings
                .AsNoTracking()
                .Where(m => m.BoardId == boardId)
                .ToListAsync(ct);

            List<MondayItemMapping> mappings;
            if (listenerState != null)
            {
                // Only process mappings created after listener start (T0).
                // Pre-existing/test mappings (CreatedAtUtc = 2000-01-01) are excluded.
                mappings = allMappings
                    .Where(m => m.CreatedAtUtc >= listenerState.StartedAtUtc)
                    .ToList();

                var excluded = allMappings.Count - mappings.Count;
                if (excluded > 0)
                {
                    _logger.LogInformation(
                        "HearingNearest: Filtered {Excluded} pre-T0 mappings (CreatedAtUtc < {T0:yyyy-MM-dd HH:mm:ss}). Remaining={Remaining}, BoardId={BoardId}",
                        excluded, listenerState.StartedAtUtc, mappings.Count, boardId);
                }
            }
            else
            {
                // No ListenerState yet — process all mappings (first run / allowlist mode)
                mappings = allMappings;
            }

            if (mappings.Count == 0)
            {
                _logger.LogDebug("No eligible Monday mappings for board {BoardId} after T0 filtering; skipping hearing sync.", boardId);
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
                // HearingStatusColumnId is a status column (type=color), not dropdown
                allowedStatusLabels = await _mondayMetadataProvider.GetAllowedStatusLabelsAsync(boardId, statusColumnId, ct);
                if (allowedStatusLabels.Count == 0)
                    _logger.LogWarning("Hearing status column {ColumnId} on board {BoardId} has no labels; status updates will be skipped.", statusColumnId, boardId);
            }

            foreach (var mapping in mappings)
            {
                if (!nearestByTik.TryGetValue(mapping.TikCounter, out var hearing))
                {
                    continue;
                }

                // Determine effective court city (City if present, else CourtName)
                var effectiveCourtCity = !string.IsNullOrWhiteSpace(hearing.City)
                    ? hearing.City.Trim()
                    : (!string.IsNullOrWhiteSpace(hearing.CourtName) ? hearing.CourtName.Trim() : null);
                
                _logger.LogDebug(
                    "Effective court city determined: TikCounter={TikCounter}, City='{City}', CourtName='{CourtName}', EffectiveCourtCity='{EffectiveCourtCity}'",
                    mapping.TikCounter,
                    hearing.City ?? "<null>",
                    hearing.CourtName ?? "<null>",
                    effectiveCourtCity ?? "<null>");

                // Check minimal required fields (only StartDate is mandatory)
                if (!hearing.StartDate.HasValue)
                {
                    _logger.LogWarning(
                        "Hearing sync skipped (missing StartDate): TikCounter={TikCounter}, MondayItemId={MondayItemId}",
                        mapping.TikCounter, mapping.MondayItemId);
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
                
                // Determine what can be updated based on available data
                var hasJudgeName = !string.IsNullOrWhiteSpace(hearing.JudgeName);
                var hasCourtCity = !string.IsNullOrWhiteSpace(effectiveCourtCity);
                var canUpdateDateHour = hasJudgeName && hasCourtCity;
                
                var judgeName = hasJudgeName ? hearing.JudgeName!.Trim() : null;
                var city = hasCourtCity ? effectiveCourtCity! : null;
                
                _logger.LogDebug(
                    "Hearing update gating: TikCounter={TikCounter}, HasJudgeName={HasJudgeName}, HasCourtCity={HasCourtCity}, CanUpdateDateHour={CanUpdateDateHour}",
                    mapping.TikCounter,
                    hasJudgeName,
                    hasCourtCity,
                    canUpdateDateHour);
                
                // Compute what changed (compare to snapshot)
                var snapshotStartUtc = snapshot?.NearestStartDateUtc;
                var snapshotStatus = snapshot?.NearestMeetStatus;
                var snapshotJudge = snapshot?.JudgeName;
                var snapshotCity = snapshot?.City;
                
                var startDateChanged = snapshotStartUtc == null || Math.Abs((startDateUtc - snapshotStartUtc.Value).TotalMinutes) > 1;
                var statusChanged = snapshotStatus == null || snapshotStatus.Value != meetStatus;
                var judgeChanged = hasJudgeName && (snapshotJudge == null || !string.Equals(snapshotJudge, judgeName, StringComparison.Ordinal));
                var cityChanged = hasCourtCity && (snapshotCity == null || !string.Equals(snapshotCity, city, StringComparison.Ordinal));
                
                var plannedSteps = new List<string>();
                
                // Status can be updated independently (not blocked by missing judge/city)
                if (statusChanged && meetStatus != 0)
                {
                    plannedSteps.Add($"SetStatus_{label}");
                }
                
                // Judge and city can be updated if they exist and changed
                if (judgeChanged || cityChanged)
                {
                    plannedSteps.Add("UpdateJudgeCity");
                }
                
                // Date/hour can ONLY be updated if BOTH JudgeName and CourtCity exist
                if (startDateChanged && canUpdateDateHour)
                {
                    plannedSteps.Add("UpdateHearingDate");
                }
                else if (startDateChanged && !canUpdateDateHour)
                {
                    _logger.LogDebug(
                        "Hearing date/hour update blocked (missing judge or court city): TikCounter={TikCounter}, TikNumber={TikNumber}, MondayItemId={MondayItemId}, HasJudgeName={HasJudgeName}, HasCourtCity={HasCourtCity}",
                        mapping.TikCounter,
                        mapping.TikNumber,
                        mapping.MondayItemId,
                        hasJudgeName,
                        hasCourtCity);
                }

                if (plannedSteps.Count == 0)
                {
                    _logger.LogDebug(
                        "Hearing sync no-op (no change): TikCounter={TikCounter}, MondayItemId={MondayItemId}, StartDate={StartDate}, MeetStatus={MeetStatus}",
                        mapping.TikCounter, mapping.MondayItemId, hearing.StartDate!.Value.ToString("yyyy-MM-dd HH:mm"), meetStatus);
                    continue;
                }

                _logger.LogDebug(
                    "Hearing sync planned: TikCounter={TikCounter}, TikNumber={TikNumber}, MondayItemId={MondayItemId}, StartDate={StartDate}, MeetStatus={MeetStatus}, Steps=[{Steps}], SnapshotOld=[StartDate={SnapshotStart}, Status={SnapshotStatus}], CanUpdateDateHour={CanUpdateDateHour}",
                    mapping.TikCounter, mapping.TikNumber, mapping.MondayItemId,
                    hearing.StartDate!.Value.ToString("yyyy-MM-dd HH:mm"), meetStatus, string.Join(", ", plannedSteps),
                    snapshotStartUtc?.ToString("yyyy-MM-dd HH:mm") ?? "<null>", snapshotStatus?.ToString() ?? "<null>", canUpdateDateHour);


                if (!enableWrites || dryRun)
                {
                    _logger.LogDebug(
                        "Hearing sync dry run: TikCounter={TikCounter}, MondayItemId={MondayItemId}, would execute steps: [{Steps}]",
                        mapping.TikCounter, mapping.MondayItemId, string.Join(", ", plannedSteps));
                    continue;
                }

                var executedSteps = new List<string>();
                var columnsToUpdate = new List<string>();
                var effectiveItemId = mapping.MondayItemId;
                
                try
                {
                    await ExecuteHearingUpdatesAsync(
                        boardId, effectiveItemId, mapping.TikCounter,
                        statusChanged, meetStatus, label, statusColumnId, allowedStatusLabels,
                        judgeChanged, cityChanged, hasJudgeName, hasCourtCity, judgeName, city,
                        startDateChanged, canUpdateDateHour, hearing,
                        executedSteps, columnsToUpdate, ct);
                }
                catch (MondayApiException apiEx) when (apiEx.IsInactiveItemError())
                {
                    // ── Monday item is inactive — skip, do NOT revive or create new item ──
                    _logger.LogWarning(
                        "Monday item inactive; skipping update (NO revive). TikCounter={TikCounter}, TikNumber={TikNumber}, BoardId={BoardId}, MondayItemId={MondayItemId}, Operation=hearing_sync",
                        mapping.TikCounter, mapping.TikNumber ?? "<null>", boardId, effectiveItemId);

                    // Persist as SyncFailure for tracking
                    try
                    {
                        _integrationDb.SyncFailures.Add(new SyncFailure
                        {
                            RunId = $"hearing_{DateTime.UtcNow:yyyyMMddHHmmss}",
                            TikCounter = mapping.TikCounter,
                            TikNumber = mapping.TikNumber,
                            BoardId = boardId,
                            Operation = "hearing_update_skipped_inactive",
                            ErrorType = "InactiveMondayItem",
                            ErrorMessage = $"Monday item {effectiveItemId} is inactive. Skipped hearing update; no revive. Error: {apiEx.Message}",
                            OccurredAtUtc = DateTime.UtcNow,
                            RetryAttempts = 0,
                            Resolved = false
                        });
                        await _integrationDb.SaveChangesAsync(ct);
                    }
                    catch (Exception persistEx)
                    {
                        _logger.LogWarning(persistEx,
                            "Failed to persist SyncFailure for inactive hearing item: TikCounter={TikCounter}",
                            mapping.TikCounter);
                    }

                    continue;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex,
                        "Hearing sync failed: TikCounter={TikCounter}, MondayItemId={MondayItemId}, PlannedSteps=[{Planned}], ExecutedSteps=[{Executed}]",
                        mapping.TikCounter, effectiveItemId, string.Join(", ", plannedSteps), string.Join(", ", executedSteps));
                    continue;
                }

                // ── Persist snapshot ──
                try
                {
                    var nowUtc = DateTime.UtcNow;
                    if (snapshot == null)
                    {
                        _integrationDb.HearingNearestSnapshots.Add(new HearingNearestSnapshot
                        {
                            TikCounter = mapping.TikCounter,
                            BoardId = boardId,
                            MondayItemId = effectiveItemId,
                            NearestStartDateUtc = startDateUtc,
                            NearestMeetStatus = meetStatus,
                            JudgeName = judgeName,
                            City = city,
                            LastSyncedAtUtc = nowUtc
                        });
                    }
                    else
                    {
                        snapshot.MondayItemId = effectiveItemId;
                        snapshot.NearestStartDateUtc = startDateUtc;
                        snapshot.NearestMeetStatus = meetStatus;
                        snapshot.JudgeName = judgeName;
                        snapshot.City = city;
                        snapshot.LastSyncedAtUtc = nowUtc;
                    }

                    await _integrationDb.SaveChangesAsync(ct);

                    _logger.LogDebug(
                        "Hearing sync succeeded: TikCounter={TikCounter}, MondayItemId={MondayItemId}, ExecutedSteps=[{Steps}], SnapshotNew=[StartDate={StartDate}, Status={MeetStatus}]",
                        mapping.TikCounter, effectiveItemId, string.Join(", ", executedSteps), startDateUtc.ToString("yyyy-MM-dd HH:mm"), meetStatus);
                }
                catch (Exception snapshotEx)
                {
                    _logger.LogError(snapshotEx,
                        "Failed to persist hearing snapshot: TikCounter={TikCounter}, MondayItemId={MondayItemId}",
                        mapping.TikCounter, effectiveItemId);
                }
            }
        }

        /// <summary>
        /// Executes the individual Monday API calls for hearing update (status, judge/city, date/hour).
        /// </summary>
        private async Task ExecuteHearingUpdatesAsync(
            long boardId, long mondayItemId, int tikCounter,
            bool statusChanged, int meetStatus, string label, string? statusColumnId, HashSet<string>? allowedStatusLabels,
            bool judgeChanged, bool cityChanged, bool hasJudgeName, bool hasCourtCity, string? judgeName, string? city,
            bool startDateChanged, bool canUpdateDateHour, OdcanitDiaryEvent hearing,
            List<string> executedSteps, List<string> columnsToUpdate,
            CancellationToken ct)
        {
            var judgeCol = _mondaySettings.JudgeNameColumnId ?? "";
            var cityCol = _mondaySettings.CourtCityColumnId ?? "";
            var dateCol = _mondaySettings.HearingDateColumnId ?? "";
            var hourCol = _mondaySettings.HearingHourColumnId ?? "";

            // Update status (independent - not blocked by missing judge/city)
            if (statusChanged && meetStatus != 0 && !string.IsNullOrWhiteSpace(statusColumnId) && allowedStatusLabels != null && allowedStatusLabels.Contains(label))
            {
                await _mondayClient.UpdateHearingStatusAsync(boardId, mondayItemId, label, statusColumnId, ct);
                executedSteps.Add($"SetStatus_{label}");
                columnsToUpdate.Add(statusColumnId);

                _logger.LogDebug(
                    "Hearing status updated: TikCounter={TikCounter}, MondayItemId={MondayItemId}, MeetStatus={MeetStatus}, Label='{Label}', ColumnId={ColumnId}",
                    tikCounter, mondayItemId, meetStatus, label, statusColumnId);
            }

            // Update judge and/or city (if they exist and changed)
            if (judgeChanged || cityChanged)
            {
                if (hasJudgeName && !string.IsNullOrWhiteSpace(judgeCol))
                    columnsToUpdate.Add(judgeCol);
                if (hasCourtCity && !string.IsNullOrWhiteSpace(cityCol))
                    columnsToUpdate.Add(cityCol);

                await _mondayClient.UpdateHearingDetailsAsync(boardId, mondayItemId, judgeName ?? "", city ?? "", judgeCol, cityCol, ct);
                executedSteps.Add("UpdateJudgeCity");

                _logger.LogDebug(
                    "Hearing details updated: TikCounter={TikCounter}, MondayItemId={MondayItemId}, JudgeName='{JudgeName}', City='{City}'",
                    tikCounter, mondayItemId, judgeName ?? "<null>", city ?? "<null>");
            }

            // Update date/hour ONLY if BOTH judge and city exist (triggers client notifications)
            if (startDateChanged && canUpdateDateHour)
            {
                await _mondayClient.UpdateHearingDateAsync(boardId, mondayItemId, hearing.StartDate!.Value, dateCol, hourCol, ct);
                executedSteps.Add("UpdateHearingDate");
                columnsToUpdate.Add(dateCol);
                columnsToUpdate.Add(hourCol);

                _logger.LogDebug(
                    "Hearing date/hour updated: TikCounter={TikCounter}, MondayItemId={MondayItemId}, StartDate={StartDate}",
                    tikCounter, mondayItemId, hearing.StartDate!.Value.ToString("yyyy-MM-dd HH:mm"));
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
