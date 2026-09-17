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

        private readonly IOdcanitReader _odcanitReader;
        private readonly IntegrationDbContext _integrationDb;
        private readonly IMondayClient _mondayClient;
        private readonly IMondayMetadataProvider _mondayMetadataProvider;
        private readonly IConfiguration _config;
        private readonly MondaySettings _mondaySettings;
        private readonly ILogger<HearingNearestSyncService> _logger;
        private readonly ISkipLogger _skipLogger;
        private readonly MondayMappingReadService _mappingReader;

        public HearingNearestSyncService(
            IOdcanitReader odcanitReader,
            IntegrationDbContext integrationDb,
            IMondayClient mondayClient,
            IMondayMetadataProvider mondayMetadataProvider,
            IConfiguration config,
            IOptions<MondaySettings> mondayOptions,
            ILogger<HearingNearestSyncService> logger,
            ISkipLogger skipLogger,
            MondayMappingReadService mappingReader)
        {
            _odcanitReader = odcanitReader;
            _integrationDb = integrationDb;
            _mondayClient = mondayClient;
            _mondayMetadataProvider = mondayMetadataProvider;
            _config = config;
            _mondaySettings = mondayOptions.Value ?? new MondaySettings();
            _logger = logger;
            _skipLogger = skipLogger;
            _mappingReader = mappingReader;
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

            // An existing, valid mapping on the target board is the reconciliation boundary.
            // Listener T0 and ReadyForMonday are onboarding-only concerns and must not
            // suppress ongoing hearing updates for an item that already exists in Monday.
            var mappings = await _mappingReader.GetAllByBoardReadOnlyAsync(boardId, ct);

            if (mappings.Count == 0)
            {
                _logger.LogDebug("No Monday mappings for board {BoardId}; skipping hearing sync.", boardId);
                return;
            }

            await ValidateMappingIdentityAsync(mappings, boardId, ct);

            var tikCounters = mappings.Select(m => m.TikCounter).Distinct().ToList();
            var diaryRows = await _odcanitReader.GetDiaryEventsByTikCountersAsync(tikCounters, ct);
            var nowLocal = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, IsraelTimeZone);
            var recoveryLookbackDays = Math.Clamp(
                _config.GetValue<int>("HearingNearest:RecoveryLookbackDays", 30),
                0,
                365);
            var nearestByTik = HearingSelector.PickNearestUpcomingHearing(
                diaryRows,
                nowLocal,
                TimeSpan.FromDays(recoveryLookbackDays));

            var mappedCases = mappings.Count;
            var selectedActive = nearestByTik.Values.Count(h => (h.MeetStatus ?? 0) == 0);
            var selectedCancelledFallback = nearestByTik.Values.Count(h => (h.MeetStatus ?? 0) == 1);
            var selectedTransferredFallback = nearestByTik.Values.Count(h => (h.MeetStatus ?? 0) == 2);
            var missingSnapshot = 0;
            var wouldUpdate = 0;
            var noChange = 0;
            var failed = 0;

            var tableExists = await TableExistsAsync(_integrationDb, "HearingNearestSnapshots", ct);
            if (!tableExists)
            {
                _logger.LogWarning(
                    "Table HearingNearestSnapshots does not exist; skipping HearingNearest sync. BoardId={BoardId}",
                    boardId);
                LogReconciliationSummary(
                    boardId,
                    mode,
                    mappedCases,
                    selectedActive,
                    selectedCancelledFallback,
                    selectedTransferredFallback,
                    missingSnapshot,
                    wouldUpdate,
                    noChange,
                    failed);
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

                var snapshot = await _integrationDb.HearingNearestSnapshots
                    .FirstOrDefaultAsync(s => s.TikCounter == mapping.TikCounter && s.BoardId == boardId, ct);
                if (snapshot == null)
                {
                    missingSnapshot++;
                }

                var startDateUtc = hearing.StartDate!.Value.Kind == DateTimeKind.Utc
                    ? hearing.StartDate.Value
                    : TimeZoneInfo.ConvertTimeToUtc(hearing.StartDate.Value, IsraelTimeZone);
                var plan = HearingNearestSyncServiceHelper.CreatePlan(
                    hearing,
                    snapshot,
                    startDateUtc,
                    effectiveCourtCity);

                if (plan.DateUpdateBlocked)
                {
                    _logger.LogDebug(
                        "Hearing date/hour update blocked (missing judge or court city): TikCounter={TikCounter}, TikNumber={TikNumber}, MondayItemId={MondayItemId}, HasJudgeName={HasJudgeName}, HasCourtCity={HasCourtCity}",
                        mapping.TikCounter,
                        mapping.TikNumber,
                        mapping.MondayItemId,
                        plan.JudgeName != null,
                        plan.City != null);
                }

                var hasSnapshotChanges = plan.StatusChanged
                    || plan.StartDateChanged
                    || plan.JudgeChanged
                    || plan.CityChanged;
                if (!hasSnapshotChanges)
                {
                    noChange++;
                    _logger.LogDebug(
                        "Hearing sync no-op (no change): TikCounter={TikCounter}, MondayItemId={MondayItemId}, StartDate={StartDate}, MeetStatus={MeetStatus}",
                        mapping.TikCounter, mapping.MondayItemId, hearing.StartDate!.Value.ToString("yyyy-MM-dd HH:mm"), plan.MeetStatus);
                    continue;
                }

                if (plan.StatusUpdateRequired
                    && (string.IsNullOrWhiteSpace(statusColumnId)
                        || allowedStatusLabels == null
                        || plan.StatusLabel == null
                        || !allowedStatusLabels.Contains(plan.StatusLabel)))
                {
                    _logger.LogWarning(
                        "Required hearing status label '{Label}' is unavailable on Monday column {ColumnId} for board {BoardId}; reconciliation and snapshot are deferred for TikCounter={TikCounter}, MondayItemId={MondayItemId}.",
                        plan.StatusLabel ?? "<null>", statusColumnId ?? "<null>", boardId, mapping.TikCounter, mapping.MondayItemId);

                    await _skipLogger.LogSkipAsync(
                        mapping.TikCounter,
                        mapping.TikNumber,
                        operation: "MondayColumnValueValidation",
                        reasonCode: "monday_invalid_status_label",
                        entityId: statusColumnId ?? string.Empty,
                        rawValue: plan.StatusLabel,
                        details: new
                        {
                            EntityType = "Status",
                            BoardId = boardId,
                            MondayItemId = mapping.MondayItemId,
                            AllowedLabelCount = allowedStatusLabels?.Count ?? 0
                        },
                        ct);
                    failed++;
                    continue;
                }

                if (plan.HasRequiredMutations)
                {
                    wouldUpdate++;
                }

                _logger.LogDebug(
                    "Hearing sync planned: TikCounter={TikCounter}, TikNumber={TikNumber}, MondayItemId={MondayItemId}, StartDate={StartDate}, MeetStatus={MeetStatus}, Steps=[{Steps}], SnapshotOld=[StartDate={SnapshotStart}, Status={SnapshotStatus}], CanUpdateDateHour={CanUpdateDateHour}",
                    mapping.TikCounter, mapping.TikNumber, mapping.MondayItemId,
                    hearing.StartDate!.Value.ToString("yyyy-MM-dd HH:mm"), plan.MeetStatus, string.Join(", ", plan.PlannedSteps),
                    snapshot?.NearestStartDateUtc?.ToString("yyyy-MM-dd HH:mm") ?? "<null>", snapshot?.NearestMeetStatus?.ToString() ?? "<null>", plan.DateUpdateRequired);


                if (!enableWrites || dryRun)
                {
                    _logger.LogDebug(
                        "Hearing sync dry run: TikCounter={TikCounter}, MondayItemId={MondayItemId}, would execute steps: [{Steps}]",
                        mapping.TikCounter, mapping.MondayItemId, string.Join(", ", plan.PlannedSteps));
                    continue;
                }

                var executedSteps = new List<string>();
                var columnsToUpdate = new List<string>();
                var effectiveItemId = mapping.MondayItemId;
                
                try
                {
                    await ExecuteHearingUpdatesAsync(
                        boardId, effectiveItemId, mapping.TikCounter,
                        plan, statusColumnId, hearing,
                        executedSteps, columnsToUpdate, ct);
                }
                catch (MondayApiException apiEx) when (apiEx.IsInactiveItemError())
                {
                    // ── Monday item is inactive — skip, do NOT revive or create new item ──
                    _logger.LogInformation(
                        "Hearing sync skipped: TikCounter={TikCounter}, TikNumber={TikNumber}, Reason=MondayItemInactive, MondayItemId={MondayItemId}, BoardId={BoardId} (no revive policy)",
                        mapping.TikCounter, mapping.TikNumber ?? "<null>", effectiveItemId, boardId);

                    // Persist as SyncFailure for tracking (classified as Ignored, not Real failure)
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

                    failed++;
                    continue;
                }
                catch (Exception ex)
                {
                    failed++;
                    _logger.LogError(ex,
                        "Hearing sync failed: TikCounter={TikCounter}, MondayItemId={MondayItemId}, PlannedSteps=[{Planned}], ExecutedSteps=[{Executed}]",
                        mapping.TikCounter, effectiveItemId, string.Join(", ", plan.PlannedSteps), string.Join(", ", executedSteps));
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
                            NearestStartDateUtc = plan.DateUpdateBlocked ? null : startDateUtc,
                            NearestMeetStatus = plan.MeetStatus,
                            JudgeName = plan.JudgeName,
                            City = plan.City,
                            LastSyncedAtUtc = nowUtc
                        });
                    }
                    else
                    {
                        snapshot.MondayItemId = effectiveItemId;
                        if (!plan.DateUpdateBlocked)
                        {
                            snapshot.NearestStartDateUtc = startDateUtc;
                        }
                        snapshot.NearestMeetStatus = plan.MeetStatus;
                        if (plan.JudgeName != null)
                        {
                            snapshot.JudgeName = plan.JudgeName;
                        }
                        if (plan.City != null)
                        {
                            snapshot.City = plan.City;
                        }
                        snapshot.LastSyncedAtUtc = nowUtc;
                    }

                    await _integrationDb.SaveChangesAsync(ct);

                    _logger.LogDebug(
                        "Hearing sync succeeded: TikCounter={TikCounter}, MondayItemId={MondayItemId}, ExecutedSteps=[{Steps}], SnapshotNew=[StartDate={StartDate}, Status={MeetStatus}]",
                        mapping.TikCounter, effectiveItemId, string.Join(", ", executedSteps), startDateUtc.ToString("yyyy-MM-dd HH:mm"), plan.MeetStatus);
                }
                catch (Exception snapshotEx)
                {
                    failed++;
                    _logger.LogError(snapshotEx,
                        "Failed to persist hearing snapshot: TikCounter={TikCounter}, MondayItemId={MondayItemId}",
                        mapping.TikCounter, effectiveItemId);
                }
            }

            LogReconciliationSummary(
                boardId,
                mode,
                mappedCases,
                selectedActive,
                selectedCancelledFallback,
                selectedTransferredFallback,
                missingSnapshot,
                wouldUpdate,
                noChange,
                failed);
        }

        /// <summary>
        /// Executes the individual Monday API calls for hearing update (status, judge/city, date/hour).
        /// </summary>
        private async Task ExecuteHearingUpdatesAsync(
            long boardId, long mondayItemId, int tikCounter,
            HearingReconciliationPlan plan, string? statusColumnId, OdcanitDiaryEvent hearing,
            List<string> executedSteps, List<string> columnsToUpdate,
            CancellationToken ct)
        {
            var judgeCol = _mondaySettings.JudgeNameColumnId ?? "";
            var cityCol = ""; // text_mkxez28d is now populated from legal UserData only, not hearing events
            var dateCol = _mondaySettings.HearingDateColumnId ?? "";
            var hourCol = _mondaySettings.HearingHourColumnId ?? "";

            // Update status (independent - not blocked by missing judge/city)
            if (plan.StatusUpdateRequired)
            {
                await _mondayClient.UpdateHearingStatusAsync(
                    boardId,
                    mondayItemId,
                    plan.StatusLabel!,
                    statusColumnId!,
                    ct);
                executedSteps.Add(plan.MeetStatus == 1 ? "SetStatus_Canceled" : "SetStatus_Transferred");
                columnsToUpdate.Add(statusColumnId!);

                _logger.LogDebug(
                    "Hearing status updated: TikCounter={TikCounter}, MondayItemId={MondayItemId}, MeetStatus={MeetStatus}, Label='{Label}', ColumnId={ColumnId}",
                    tikCounter, mondayItemId, plan.MeetStatus, plan.StatusLabel, statusColumnId);
            }

            // Update judge and/or city (if they exist and changed)
            if (plan.JudgeOrCityUpdateRequired)
            {
                if (plan.JudgeName != null && !string.IsNullOrWhiteSpace(judgeCol))
                    columnsToUpdate.Add(judgeCol);
                if (plan.City != null && !string.IsNullOrWhiteSpace(cityCol))
                    columnsToUpdate.Add(cityCol);

                await _mondayClient.UpdateHearingDetailsAsync(
                    boardId,
                    mondayItemId,
                    plan.JudgeName ?? "",
                    plan.City ?? "",
                    judgeCol,
                    cityCol,
                    ct);
                executedSteps.Add("UpdateJudgeCity");

                _logger.LogDebug(
                    "Hearing details updated: TikCounter={TikCounter}, MondayItemId={MondayItemId}, JudgeName='{JudgeName}', City='{City}'",
                    tikCounter, mondayItemId, plan.JudgeName ?? "<null>", plan.City ?? "<null>");
            }

            // Update date/hour ONLY if BOTH judge and city exist (triggers client notifications)
            if (plan.DateUpdateRequired)
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

        private void LogReconciliationSummary(
            long boardId,
            string mode,
            int mappedCases,
            int selectedActive,
            int selectedCancelledFallback,
            int selectedTransferredFallback,
            int missingSnapshot,
            int wouldUpdate,
            int noChange,
            int failed)
        {
            _logger.LogInformation(
                "HearingNearest reconciliation summary: BoardId={BoardId}, Mode={Mode}, MappedCases={MappedCases}, SelectedActive={SelectedActive}, SelectedCancelledFallback={SelectedCancelledFallback}, SelectedTransferredFallback={SelectedTransferredFallback}, MissingSnapshot={MissingSnapshot}, WouldUpdate={WouldUpdate}, NoChange={NoChange}, Failed={Failed}",
                boardId,
                mode,
                mappedCases,
                selectedActive,
                selectedCancelledFallback,
                selectedTransferredFallback,
                missingSnapshot,
                wouldUpdate,
                noChange,
                failed);
        }

        private async Task ValidateMappingIdentityAsync(
            IReadOnlyCollection<MondayItemMapping> mappings,
            long boardId,
            CancellationToken ct)
        {
            var issues = new List<string>();

            foreach (var mapping in mappings)
            {
                if (mapping.TikCounter <= 0)
                {
                    issues.Add($"Id={mapping.Id},Item={mapping.MondayItemId},TikCounter={mapping.TikCounter},TikNumber={mapping.TikNumber ?? "<null>"},Reason=non_positive_counter");
                }

                if (mapping.BoardId != boardId)
                {
                    issues.Add($"Id={mapping.Id},Item={mapping.MondayItemId},TikCounter={mapping.TikCounter},TikNumber={mapping.TikNumber ?? "<null>"},Reason=board_mismatch");
                }

                if (mapping.MondayItemId <= 0)
                {
                    issues.Add($"Id={mapping.Id},Item={mapping.MondayItemId},TikCounter={mapping.TikCounter},TikNumber={mapping.TikNumber ?? "<null>"},Reason=invalid_monday_item_id");
                }

                if (string.IsNullOrWhiteSpace(mapping.TikNumber))
                {
                    issues.Add($"Id={mapping.Id},Item={mapping.MondayItemId},TikCounter={mapping.TikCounter},TikNumber=<null>,Reason=missing_tik_number");
                }
            }

            var tikNumbers = mappings
                .Where(m => !string.IsNullOrWhiteSpace(m.TikNumber))
                .Select(m => m.TikNumber!)
                .Distinct(StringComparer.Ordinal)
                .ToList();

            var resolved = await _odcanitReader.ResolveTikNumbersToCountersAsync(tikNumbers, ct);
            foreach (var mapping in mappings.Where(m => !string.IsNullOrWhiteSpace(m.TikNumber)))
            {
                if (!resolved.TryGetValue(mapping.TikNumber!.Trim(), out var realTikCounter))
                {
                    _logger.LogWarning(
                        "HEARING_NEAREST | Mapping TikNumber could not be resolved by Odcanit reader; treating as inconclusive, not fatal. MappingId={MappingId}, BoardId={BoardId}, MondayItemId={MondayItemId}, TikCounter={TikCounter}, TikNumber={TikNumber}",
                        mapping.Id,
                        mapping.BoardId,
                        mapping.MondayItemId,
                        mapping.TikCounter,
                        mapping.TikNumber);
                    continue;
                }

                if (mapping.TikCounter != realTikCounter)
                {
                    issues.Add($"Id={mapping.Id},Item={mapping.MondayItemId},TikCounter={mapping.TikCounter},TikNumber={mapping.TikNumber},RealTikCounter={realTikCounter},Reason=tik_counter_mismatch");
                }
            }

            if (issues.Count == 0)
            {
                return;
            }

            var message =
                $"HearingNearest mapping integrity failed for BoardId={boardId}. " +
                "A hearing cancellation/transfer could be missed unless mappings use real Odcanit counters. " +
                $"Issues={string.Join(" | ", issues.Take(20))}";

            _logger.LogCritical("HEARING_NEAREST | {Message}", message);
            throw new MondayItemMappingIntegrityException(message);
        }

        /// <summary>
        /// Checks if a table exists in the database using sys.tables (does not reference the table itself).
        /// </summary>
        private static async Task<bool> TableExistsAsync(IntegrationDbContext db, string tableName, CancellationToken ct)
        {
            if (!db.Database.IsRelational())
            {
                return true;
            }

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
