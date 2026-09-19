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
    public enum HearingNearestMode
    {
        Disabled = 0,
        StatusOnly = 1,
        Full = 2
    }

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
        private readonly HearingStatusWorkflowService _statusWorkflow;

        public HearingNearestSyncService(
            IOdcanitReader odcanitReader,
            IntegrationDbContext integrationDb,
            IMondayClient mondayClient,
            IMondayMetadataProvider mondayMetadataProvider,
            IConfiguration config,
            IOptions<MondaySettings> mondayOptions,
            ILogger<HearingNearestSyncService> logger,
            ISkipLogger skipLogger,
            MondayMappingReadService mappingReader,
            HearingStatusWorkflowService statusWorkflow)
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
            _statusWorkflow = statusWorkflow;
        }

        /// <summary>
        /// Syncs nearest upcoming hearing per TikCounter to Monday. Uses OdcanitWrites:Enable and DryRun.
        /// </summary>
        public async Task SyncNearestHearingsAsync(long boardId, CancellationToken ct)
        {
            var configuredMode = _config["HearingNearest:Mode"] ?? "Disabled";
            if (!Enum.TryParse<HearingNearestMode>(configuredMode, ignoreCase: true, out var hearingMode) ||
                !Enum.IsDefined(hearingMode))
            {
                _logger.LogError(
                    "HearingNearest:Mode is invalid; hearing reconciliation is disabled fail-closed.");
                return;
            }
            if (hearingMode == HearingNearestMode.Disabled)
            {
                _logger.LogDebug("HearingNearest reconciliation is disabled by configuration.");
                return;
            }

            var testingEnabled = _config.GetValue<bool>("Testing:Enable", false);
            if (testingEnabled)
            {
                _logger.LogInformation(
                    "Testing.Enable=true - skipping HearingNearestSyncService (no Odcanit access). BoardId={BoardId}",
                    boardId);
                return;
            }

            var enableWrites = _config.GetValue<bool>("OdcanitWrites:Enable", false);
            var dryRun = !enableWrites
                || _config.GetValue<bool>("OdcanitWrites:DryRun", true)
                || _config.GetValue<bool>("HearingNearest:DryRun", true);
            var mode = $"{hearingMode}:{(dryRun ? "dryrun" : "live")}";

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
            var diaryRowsByTik = diaryRows
                .Where(row => row.TikCounter.HasValue)
                .GroupBy(row => row.TikCounter!.Value)
                .ToDictionary(group => group.Key, group => group.ToList());

            var mappedCases = mappings.Count;
            var selectedActive = nearestByTik.Values.Count(h => (h.MeetStatus ?? 0) == 0);
            var selectedCancelledFallback = nearestByTik.Values.Count(h => (h.MeetStatus ?? 0) == 1);
            var selectedTransferredFallback = nearestByTik.Values.Count(h => (h.MeetStatus ?? 0) == 2);
            var missingSnapshot = 0;
            var wouldUpdate = 0;
            var noChange = 0;
            var failed = 0;
            var skippedUnavailable = 0;
            var protectedWorkflowStatus = 0;
            var advancedAfterMutation = 0;
            var legacyStatusAmbiguous = 0;
            var unobservedTerminalAmbiguous = 0;
            var deliveryUncertain = 0;

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
            if (string.IsNullOrWhiteSpace(statusColumnId))
            {
                throw new InvalidOperationException(
                    "Hearing status reconciliation requires a configured status column.");
            }
            var boardColumns = await _mondayMetadataProvider.GetBoardColumnsMetadataAsync(boardId, ct);
            if (!boardColumns.ContainsKey(statusColumnId))
            {
                throw new InvalidOperationException(
                    "Hearing status reconciliation failed: configured status column is absent.");
            }
            var allowedStatusLabels = await _mondayMetadataProvider.GetAllowedStatusLabelsAsync(
                boardId,
                statusColumnId,
                ct);
            HearingStatusWorkflowService.ValidateManagedLabels(allowedStatusLabels);

            foreach (var mapping in mappings)
            {
                if (!diaryRowsByTik.TryGetValue(mapping.TikCounter, out var caseRows) || caseRows.Count == 0)
                {
                    continue;
                }

                nearestByTik.TryGetValue(mapping.TikCounter, out var hearing);

                var snapshot = await _integrationDb.HearingNearestSnapshots
                    .FirstOrDefaultAsync(s => s.TikCounter == mapping.TikCounter && s.BoardId == boardId, ct);
                if (snapshot == null)
                {
                    missingSnapshot++;
                }

                OdcanitDiaryEvent? trackedEvent = null;
                if (snapshot?.ObservedSourceEventId is int trackedSourceEventId)
                {
                    var trackedMatches = caseRows
                        .Where(row => row.SourceEventId == trackedSourceEventId)
                        .ToArray();
                    if (trackedMatches.Length > 1)
                    {
                        failed++;
                        _logger.LogWarning(
                            "Hearing reconciliation failed safely: TikCounter={TikCounter}, MondayItemId={MondayItemId}, Reason=DuplicateSourceEventIdentity",
                            mapping.TikCounter,
                            mapping.MondayItemId);
                        continue;
                    }
                    trackedEvent = trackedMatches.SingleOrDefault();
                }

                var selectedIsActive = hearing != null && (hearing.MeetStatus ?? 0) == 0;
                var observedEvent = selectedIsActive ? hearing : trackedEvent ?? hearing;
                var trackedStatus = trackedEvent?.MeetStatus ?? 0;
                var hasActionableTrackedStatus = trackedEvent != null && trackedStatus is 1 or 2;
                var statusAlreadyDelivered = hasActionableTrackedStatus &&
                    snapshot!.DeliveredStatusSourceEventId == trackedEvent!.SourceEventId &&
                    snapshot.DeliveredMeetStatus == trackedStatus;
                var statusEvent = hasActionableTrackedStatus && !statusAlreadyDelivered
                    ? trackedEvent
                    : null;

                // A cancelled/transferred row is actionable only after its stable
                // source event ID was previously observed. Legacy rows without that
                // history are intentionally not baselined or treated as delivered.
                if (snapshot?.ObservedSourceEventId == null)
                {
                    if (hearing == null)
                    {
                        continue;
                    }
                    if (!hearing.SourceEventId.HasValue || (hearing.MeetStatus ?? 0) is 1 or 2)
                    {
                        legacyStatusAmbiguous++;
                        _logger.LogWarning(
                            "Hearing status reconciliation skipped safely: TikCounter={TikCounter}, MondayItemId={MondayItemId}, Reason=LegacyStatusAmbiguous",
                            mapping.TikCounter,
                            mapping.MondayItemId);
                        continue;
                    }

                    observedEvent = hearing;
                    statusEvent = null;
                }
                else if (trackedEvent == null)
                {
                    if (hearing?.SourceEventId.HasValue == true &&
                        (hearing.MeetStatus ?? 0) is 1 or 2)
                    {
                        unobservedTerminalAmbiguous++;
                        _logger.LogWarning(
                            "Hearing status reconciliation skipped safely: TikCounter={TikCounter}, MondayItemId={MondayItemId}, SourceEventId={SourceEventId}, Reason=UnobservedTerminalEventAmbiguous",
                            mapping.TikCounter,
                            mapping.MondayItemId,
                            hearing.SourceEventId);
                        continue;
                    }

                    if (!selectedIsActive || !hearing!.SourceEventId.HasValue)
                    {
                        legacyStatusAmbiguous++;
                        _logger.LogWarning(
                            "Hearing status reconciliation skipped safely: TikCounter={TikCounter}, MondayItemId={MondayItemId}, Reason=TrackedSourceEventUnavailable",
                            mapping.TikCounter,
                            mapping.MondayItemId);
                        continue;
                    }

                    observedEvent = hearing;
                    statusEvent = null;
                }

                // With no selected future or bounded-recovery candidate, do not
                // reintroduce an older tracked row merely because its ID is known.
                if (hearing == null)
                {
                    continue;
                }

                if (observedEvent?.SourceEventId == null)
                {
                    legacyStatusAmbiguous++;
                    _logger.LogWarning(
                        "Hearing status reconciliation skipped safely: TikCounter={TikCounter}, MondayItemId={MondayItemId}, Reason=SourceEventIdentityUnavailable",
                        mapping.TikCounter,
                        mapping.MondayItemId);
                    continue;
                }

                // Schedule fields follow the selected active replacement when one
                // exists; otherwise they remain associated with the tracked event.
                var hearingForPlan = selectedIsActive ? hearing! : observedEvent;

                // Determine effective court city (City if present, else CourtName)
                var effectiveCourtCity = !string.IsNullOrWhiteSpace(hearingForPlan.City)
                    ? hearingForPlan.City.Trim()
                    : (!string.IsNullOrWhiteSpace(hearingForPlan.CourtName) ? hearingForPlan.CourtName.Trim() : null);
                
                _logger.LogDebug(
                    "Effective court city determined: TikCounter={TikCounter}, City='{City}', CourtName='{CourtName}', EffectiveCourtCity='{EffectiveCourtCity}'",
                    mapping.TikCounter,
                    hearingForPlan.City ?? "<null>",
                    hearingForPlan.CourtName ?? "<null>",
                    effectiveCourtCity ?? "<null>");

                // Check minimal required fields (only StartDate is mandatory)
                if (!hearingForPlan.StartDate.HasValue)
                {
                    _logger.LogWarning(
                        "Hearing sync skipped (missing StartDate): TikCounter={TikCounter}, MondayItemId={MondayItemId}",
                        mapping.TikCounter, mapping.MondayItemId);
                    continue;
                }

                var startDateUtc = hearingForPlan.StartDate!.Value.Kind == DateTimeKind.Utc
                    ? hearingForPlan.StartDate.Value
                    : TimeZoneInfo.ConvertTimeToUtc(hearingForPlan.StartDate.Value, IsraelTimeZone);
                var plan = HearingNearestSyncServiceHelper.CreatePlan(
                    hearingForPlan,
                    snapshot,
                    startDateUtc,
                    effectiveCourtCity);

                var executedSteps = new List<string>();
                var columnsToUpdate = new List<string>();
                var effectiveItemId = mapping.MondayItemId;
                var hadPendingDelivery = statusEvent != null &&
                    snapshot!.PendingStatusSourceEventId == statusEvent.SourceEventId &&
                    snapshot.PendingMeetStatus == statusEvent.MeetStatus;

                if (statusEvent != null &&
                    snapshot!.PendingStatusSourceEventId.HasValue &&
                    !hadPendingDelivery)
                {
                    deliveryUncertain++;
                    _logger.LogWarning(
                        "Hearing status reconciliation quarantined: TikCounter={TikCounter}, MondayItemId={MondayItemId}, Reason=PendingDeliveryConflict",
                        mapping.TikCounter,
                        effectiveItemId);
                    continue;
                }

                // Before the first live mutation, durably record the exact event/status
                // tuple being attempted. If the request times out or the process exits
                // after Monday accepts it, the next cycle verifies this pending tuple
                // instead of blindly publishing it again.
                if (statusEvent != null && !dryRun && !hadPendingDelivery)
                {
                    var preflight = await _statusWorkflow.ReconcileAsync(
                        boardId,
                        effectiveItemId,
                        statusColumnId,
                        statusEvent.MeetStatus ?? 0,
                        live: false,
                        ct,
                        allowProtectedTransition: true,
                        protectedReadbackConfirmsSuccess: false,
                        forceManagedWrite: true);

                    if (preflight.Outcome == HearingStatusWorkflowOutcome.SkippedUnavailable)
                    {
                        skippedUnavailable++;
                        _logger.LogInformation(
                            "Hearing status reconciliation skipped safely: TikCounter={TikCounter}, MondayItemId={MondayItemId}, Reason=MondayItemUnavailable",
                            mapping.TikCounter,
                            effectiveItemId);
                        continue;
                    }
                    if (preflight.Outcome != HearingStatusWorkflowOutcome.Planned)
                    {
                        failed++;
                        _logger.LogWarning(
                            "Hearing status reconciliation preflight failed safely: TikCounter={TikCounter}, MondayItemId={MondayItemId}, Outcome={Outcome}, Reason={Reason}",
                            mapping.TikCounter,
                            effectiveItemId,
                            preflight.Outcome,
                            preflight.ReasonCode ?? "unspecified");
                        continue;
                    }

                    snapshot!.PendingStatusSourceEventId = statusEvent.SourceEventId;
                    snapshot.PendingMeetStatus = statusEvent.MeetStatus;
                    snapshot.PendingStatusSinceUtc = DateTime.UtcNow;
                    snapshot.PendingInitialStatusWasDesired = string.Equals(
                        preflight.ObservedLabel,
                        preflight.DesiredLabel,
                        StringComparison.Ordinal);
                    try
                    {
                        await _integrationDb.SaveChangesAsync(ct);
                    }
                    catch (Exception pendingEx)
                    {
                        failed++;
                        _logger.LogError(
                            pendingEx,
                            "Failed to persist pending hearing status delivery: TikCounter={TikCounter}, MondayItemId={MondayItemId}",
                            mapping.TikCounter,
                            effectiveItemId);
                        continue;
                    }
                }

                var statusResult = await _statusWorkflow.ReconcileAsync(
                    boardId,
                    effectiveItemId,
                    statusColumnId,
                    statusEvent?.MeetStatus ?? 0,
                    live: !dryRun,
                    ct,
                    allowProtectedTransition: statusEvent != null && !hadPendingDelivery,
                    protectedReadbackConfirmsSuccess: statusEvent == null || !hadPendingDelivery,
                    forceManagedWrite: statusEvent != null && !hadPendingDelivery);

                if (statusEvent != null &&
                    hadPendingDelivery &&
                    statusResult.Outcome == HearingStatusWorkflowOutcome.AlreadyCorrect &&
                    snapshot!.PendingInitialStatusWasDesired != false)
                {
                    deliveryUncertain++;
                    _logger.LogWarning(
                        "Hearing status delivery remains uncertain: TikCounter={TikCounter}, MondayItemId={MondayItemId}, Reason=PendingExactStatePreexisted",
                        mapping.TikCounter,
                        effectiveItemId);
                    continue;
                }

                var statusDeliverySucceeded = statusEvent != null && statusResult.Outcome is
                    HearingStatusWorkflowOutcome.AlreadyCorrect or
                    HearingStatusWorkflowOutcome.Updated or
                    HearingStatusWorkflowOutcome.AdvancedAfterMutation;

                switch (statusResult.Outcome)
                {
                    case HearingStatusWorkflowOutcome.Planned:
                        wouldUpdate++;
                        break;
                    case HearingStatusWorkflowOutcome.ProtectedWorkflowStatus:
                        if (statusEvent != null && hadPendingDelivery)
                        {
                            deliveryUncertain++;
                            _logger.LogWarning(
                                "Hearing status delivery remains uncertain: TikCounter={TikCounter}, MondayItemId={MondayItemId}, Reason=PendingDeliveryProtectedWorkflowState",
                                mapping.TikCounter,
                                effectiveItemId);
                            continue;
                        }
                        protectedWorkflowStatus++;
                        break;
                    case HearingStatusWorkflowOutcome.SkippedUnavailable:
                        skippedUnavailable++;
                        _logger.LogInformation(
                            "Hearing status reconciliation skipped safely: TikCounter={TikCounter}, MondayItemId={MondayItemId}, Reason=MondayItemUnavailable",
                            mapping.TikCounter,
                            effectiveItemId);
                        continue;
                    case HearingStatusWorkflowOutcome.AdvancedAfterMutation:
                        advancedAfterMutation++;
                        executedSteps.Add("StatusAdvancedByWorkflow");
                        break;
                    case HearingStatusWorkflowOutcome.Updated:
                        executedSteps.Add(statusEvent!.MeetStatus == 1
                            ? "SetStatus_Canceled"
                            : "SetStatus_Transferred");
                        columnsToUpdate.Add(statusColumnId);
                        break;
                    case HearingStatusWorkflowOutcome.ValidationFailed:
                    case HearingStatusWorkflowOutcome.MondayFailed:
                    case HearingStatusWorkflowOutcome.VerificationFailed:
                        failed++;
                        _logger.LogWarning(
                            "Hearing status reconciliation failed safely: TikCounter={TikCounter}, MondayItemId={MondayItemId}, Outcome={Outcome}, Reason={Reason}",
                            mapping.TikCounter,
                            effectiveItemId,
                            statusResult.Outcome,
                            statusResult.ReasonCode ?? "unspecified");
                        continue;
                }

                var hasNonStatusChanges = plan.JudgeOrCityUpdateRequired || plan.DateUpdateRequired;
                if (hearingMode == HearingNearestMode.StatusOnly)
                {
                    hasNonStatusChanges = false;
                }

                if (dryRun)
                {
                    continue;
                }

                if (hearingMode == HearingNearestMode.Full && hasNonStatusChanges)
                {
                    try
                    {
                        await ExecuteNonStatusHearingUpdatesAsync(
                            boardId,
                            effectiveItemId,
                            mapping.TikCounter,
                            plan,
                            hearingForPlan,
                            executedSteps,
                            columnsToUpdate,
                            ct);
                    }
                    catch (Exception ex)
                    {
                        failed++;
                        _logger.LogError(ex,
                            "Hearing non-status reconciliation failed: TikCounter={TikCounter}, MondayItemId={MondayItemId}",
                            mapping.TikCounter,
                            effectiveItemId);
                        continue;
                    }
                }

                var statusSnapshotChanged = snapshot == null
                    || snapshot.MondayItemId != effectiveItemId
                    || snapshot.ObservedSourceEventId != observedEvent.SourceEventId
                    || snapshot.NearestMeetStatus != (observedEvent.MeetStatus ?? 0)
                    || (statusDeliverySucceeded &&
                        (snapshot.DeliveredStatusSourceEventId != statusEvent!.SourceEventId ||
                         snapshot.DeliveredMeetStatus != statusEvent.MeetStatus ||
                         snapshot.PendingStatusSourceEventId.HasValue ||
                         snapshot.PendingMeetStatus.HasValue ||
                         snapshot.PendingStatusSinceUtc.HasValue ||
                         snapshot.PendingInitialStatusWasDesired.HasValue));
                var fullSnapshotChanged = statusSnapshotChanged
                    || plan.StartDateChanged
                    || plan.JudgeChanged
                    || plan.CityChanged;
                if ((hearingMode == HearingNearestMode.StatusOnly && !statusSnapshotChanged) ||
                    (hearingMode == HearingNearestMode.Full && !fullSnapshotChanged))
                {
                    noChange++;
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
                            ObservedSourceEventId = observedEvent.SourceEventId,
                            NearestStartDateUtc = hearingMode == HearingNearestMode.StatusOnly
                                ? null
                                : plan.DateUpdateBlocked ? null : startDateUtc,
                            NearestMeetStatus = observedEvent.MeetStatus ?? 0,
                            DeliveredStatusSourceEventId = statusDeliverySucceeded ? statusEvent!.SourceEventId : null,
                            DeliveredMeetStatus = statusDeliverySucceeded ? statusEvent!.MeetStatus : null,
                            JudgeName = hearingMode == HearingNearestMode.StatusOnly ? null : plan.JudgeName,
                            City = hearingMode == HearingNearestMode.StatusOnly ? null : plan.City,
                            LastSyncedAtUtc = nowUtc
                        });
                    }
                    else
                    {
                        snapshot.MondayItemId = effectiveItemId;
                        snapshot.ObservedSourceEventId = observedEvent.SourceEventId;
                        if (hearingMode == HearingNearestMode.Full && !plan.DateUpdateBlocked)
                        {
                            snapshot.NearestStartDateUtc = startDateUtc;
                        }
                        snapshot.NearestMeetStatus = observedEvent.MeetStatus ?? 0;
                        if (statusDeliverySucceeded)
                        {
                            snapshot.DeliveredStatusSourceEventId = statusEvent!.SourceEventId;
                            snapshot.DeliveredMeetStatus = statusEvent.MeetStatus;
                            snapshot.PendingStatusSourceEventId = null;
                            snapshot.PendingMeetStatus = null;
                            snapshot.PendingStatusSinceUtc = null;
                            snapshot.PendingInitialStatusWasDesired = null;
                        }
                        if (hearingMode == HearingNearestMode.Full && plan.JudgeName != null)
                        {
                            snapshot.JudgeName = plan.JudgeName;
                        }
                        if (hearingMode == HearingNearestMode.Full && plan.City != null)
                        {
                            snapshot.City = plan.City;
                        }
                        snapshot.LastSyncedAtUtc = nowUtc;
                    }

                    await _integrationDb.SaveChangesAsync(ct);

                    _logger.LogDebug(
                        "Hearing sync succeeded: TikCounter={TikCounter}, MondayItemId={MondayItemId}, ExecutedSteps=[{Steps}], SnapshotNew=[StartDate={StartDate}, Status={MeetStatus}]",
                        mapping.TikCounter, effectiveItemId, string.Join(", ", executedSteps), startDateUtc.ToString("yyyy-MM-dd HH:mm"), observedEvent.MeetStatus ?? 0);
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
                failed,
                skippedUnavailable,
                protectedWorkflowStatus,
                advancedAfterMutation,
                legacyStatusAmbiguous,
                unobservedTerminalAmbiguous,
                deliveryUncertain);
        }

        /// <summary>
        /// Executes only the non-status Monday hearing mutations. Status is
        /// reconciled separately through HearingStatusWorkflowService.
        /// </summary>
        private async Task ExecuteNonStatusHearingUpdatesAsync(
            long boardId, long mondayItemId, int tikCounter,
            HearingReconciliationPlan plan, OdcanitDiaryEvent hearing,
            List<string> executedSteps, List<string> columnsToUpdate,
            CancellationToken ct)
        {
            var judgeCol = _mondaySettings.JudgeNameColumnId ?? "";
            var cityCol = ""; // text_mkxez28d is now populated from legal UserData only, not hearing events
            var dateCol = _mondaySettings.HearingDateColumnId ?? "";
            var hourCol = _mondaySettings.HearingHourColumnId ?? "";

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
            int failed,
            int skippedUnavailable = 0,
            int protectedWorkflowStatus = 0,
            int advancedAfterMutation = 0,
            int legacyStatusAmbiguous = 0,
            int unobservedTerminalAmbiguous = 0,
            int deliveryUncertain = 0)
        {
            _logger.LogInformation(
                "HearingNearest reconciliation summary: BoardId={BoardId}, Mode={Mode}, MappedCases={MappedCases}, SelectedActive={SelectedActive}, SelectedCancelledFallback={SelectedCancelledFallback}, SelectedTransferredFallback={SelectedTransferredFallback}, MissingSnapshot={MissingSnapshot}, WouldUpdate={WouldUpdate}, SkippedUnavailable={SkippedUnavailable}, ProtectedWorkflowStatus={ProtectedWorkflowStatus}, AdvancedAfterMutation={AdvancedAfterMutation}, LegacyStatusAmbiguous={LegacyStatusAmbiguous}, UnobservedTerminalAmbiguous={UnobservedTerminalAmbiguous}, DeliveryUncertain={DeliveryUncertain}, NoChange={NoChange}, Failed={Failed}",
                boardId,
                mode,
                mappedCases,
                selectedActive,
                selectedCancelledFallback,
                selectedTransferredFallback,
                missingSnapshot,
                wouldUpdate,
                skippedUnavailable,
                protectedWorkflowStatus,
                advancedAfterMutation,
                legacyStatusAmbiguous,
                unobservedTerminalAmbiguous,
                deliveryUncertain,
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
