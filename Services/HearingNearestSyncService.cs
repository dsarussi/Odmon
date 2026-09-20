using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Globalization;
using System.Text.Json;
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
            var fullDetailTikCounters = _config
                .GetSection("HearingNearest:FullDetailTikCounters")
                .Get<int[]>()?
                .Where(value => value > 0)
                .ToHashSet() ?? new HashSet<int>();

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
            var proposedDateChanges = 0;
            var proposedTimeChanges = 0;
            var proposedJudgeChanges = 0;
            var detailBaselineInitializations = 0;
            var blockedMissingJudge = 0;
            var detailScopeExcluded = 0;

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
            if (hearingMode == HearingNearestMode.Full)
            {
                ValidateFullModeColumns(boardColumns);
            }

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

                // Check minimal required fields (only StartDate is mandatory)
                if (!hearingForPlan.StartDate.HasValue)
                {
                    _logger.LogWarning(
                        "Hearing sync skipped (missing StartDate): TikCounter={TikCounter}, MondayItemId={MondayItemId}",
                        mapping.TikCounter, mapping.MondayItemId);
                    continue;
                }

                var startDateUtc = ToSnapshotUtc(hearingForPlan.StartDate!.Value);
                var executedSteps = new List<string>();
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

                // Full mode commits a confirmed status delivery before any detail
                // work. A later detail failure therefore remains retryable without
                // reopening or republishing the delivered event/status tuple.
                if (hearingMode == HearingNearestMode.Full && statusDeliverySucceeded && !dryRun)
                {
                    try
                    {
                        snapshot!.DeliveredStatusSourceEventId = statusEvent!.SourceEventId;
                        snapshot.DeliveredMeetStatus = statusEvent.MeetStatus;
                        snapshot.PendingStatusSourceEventId = null;
                        snapshot.PendingMeetStatus = null;
                        snapshot.PendingStatusSinceUtc = null;
                        snapshot.PendingInitialStatusWasDesired = null;
                        await _integrationDb.SaveChangesAsync(ct);
                    }
                    catch (Exception deliveryStateEx)
                    {
                        failed++;
                        _logger.LogError(
                            deliveryStateEx,
                            "Failed to persist confirmed hearing status delivery: TikCounter={TikCounter}, MondayItemId={MondayItemId}",
                            mapping.TikCounter,
                            effectiveItemId);
                        continue;
                    }
                }

                HearingDetailsReconciliationResult detailsResult = HearingDetailsReconciliationResult.NotApplicable;
                var detailInConfiguredScope = fullDetailTikCounters.Count == 0 ||
                    fullDetailTikCounters.Contains(mapping.TikCounter);
                if (hearingMode == HearingNearestMode.Full && selectedIsActive && detailInConfiguredScope)
                {
                    try
                    {
                        detailsResult = await ReconcileActiveHearingDetailsAsync(
                            boardId,
                            effectiveItemId,
                            mapping.TikCounter,
                            hearingForPlan,
                            snapshot,
                            startDateUtc,
                            live: !dryRun,
                            ct);
                        proposedDateChanges += detailsResult.DateChangePlanned ? 1 : 0;
                        proposedTimeChanges += detailsResult.TimeChangePlanned ? 1 : 0;
                        proposedJudgeChanges += detailsResult.JudgeChangePlanned ? 1 : 0;
                        detailBaselineInitializations += detailsResult.BaselineInitialization ? 1 : 0;
                        blockedMissingJudge += detailsResult.JudgeBlocked ? 1 : 0;
                        if (detailsResult.Unavailable)
                        {
                            skippedUnavailable++;
                            continue;
                        }
                        if (!detailsResult.Succeeded)
                        {
                            failed++;
                            continue;
                        }
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
                else if (hearingMode == HearingNearestMode.Full && selectedIsActive)
                {
                    detailScopeExcluded++;
                }

                if (dryRun)
                {
                    continue;
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
                    || detailsResult.PersistDateTime
                    || detailsResult.PersistJudge
                    || detailsResult.MutationPerformed;
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
                            NearestStartDateUtc = hearingMode == HearingNearestMode.Full && detailsResult.PersistDateTime
                                ? startDateUtc
                                : null,
                            NearestMeetStatus = observedEvent.MeetStatus ?? 0,
                            DeliveredStatusSourceEventId = statusDeliverySucceeded ? statusEvent!.SourceEventId : null,
                            DeliveredMeetStatus = statusDeliverySucceeded ? statusEvent!.MeetStatus : null,
                            JudgeName = hearingMode == HearingNearestMode.Full && detailsResult.PersistJudge
                                ? detailsResult.JudgeName
                                : null,
                            City = null,
                            LastSyncedAtUtc = nowUtc
                        });
                    }
                    else
                    {
                        snapshot.MondayItemId = effectiveItemId;
                        snapshot.ObservedSourceEventId = observedEvent.SourceEventId;
                        if (hearingMode == HearingNearestMode.Full && detailsResult.PersistDateTime)
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
                        if (hearingMode == HearingNearestMode.Full && detailsResult.PersistJudge)
                        {
                            snapshot.JudgeName = detailsResult.JudgeName;
                        }
                        snapshot.LastSyncedAtUtc = nowUtc;
                    }

                    await _integrationDb.SaveChangesAsync(ct);

                    _logger.LogDebug(
                        "Hearing sync succeeded: TikCounter={TikCounter}, MondayItemId={MondayItemId}, ExecutedSteps=[{Steps}], MeetStatus={MeetStatus}",
                        mapping.TikCounter, effectiveItemId, string.Join(", ", executedSteps), observedEvent.MeetStatus ?? 0);
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
                deliveryUncertain,
                proposedDateChanges,
                proposedTimeChanges,
                proposedJudgeChanges,
                detailBaselineInitializations,
                blockedMissingJudge,
                detailScopeExcluded);
        }

        private void ValidateFullModeColumns(IReadOnlyDictionary<string, BoardColumnMetadata> boardColumns)
        {
            var requiredColumns = new[]
            {
                (ColumnId: _mondaySettings.HearingDateColumnId, Type: "date"),
                (ColumnId: _mondaySettings.HearingHourColumnId, Type: "hour"),
                (ColumnId: _mondaySettings.JudgeNameColumnId, Type: "text")
            };
            if (requiredColumns.Any(required => string.IsNullOrWhiteSpace(required.ColumnId)) ||
                requiredColumns.Any(required =>
                    !boardColumns.TryGetValue(required.ColumnId!, out var metadata) ||
                    !string.Equals(metadata.ColumnType, required.Type, StringComparison.OrdinalIgnoreCase)))
            {
                throw new InvalidOperationException(
                    "HearingNearest Full mode requires configured date, hour, and text judge columns on the target board.");
            }
        }

        private async Task<HearingDetailsReconciliationResult> ReconcileActiveHearingDetailsAsync(
            long boardId,
            long mondayItemId,
            int tikCounter,
            OdcanitDiaryEvent hearing,
            HearingNearestSnapshot? snapshot,
            DateTime sourceStartUtc,
            bool live,
            CancellationToken ct)
        {
            var dateColumnId = _mondaySettings.HearingDateColumnId!;
            var hourColumnId = _mondaySettings.HearingHourColumnId!;
            var judgeColumnId = _mondaySettings.JudgeNameColumnId!;
            var current = await _mondayClient.GetHearingDetailsValueAsync(
                boardId,
                mondayItemId,
                dateColumnId,
                hourColumnId,
                judgeColumnId,
                ct);
            if (current == null)
            {
                _logger.LogInformation(
                    "Hearing details reconciliation skipped safely: TikCounter={TikCounter}, MondayItemId={MondayItemId}, Reason=MondayItemUnavailable",
                    tikCounter,
                    mondayItemId);
                return HearingDetailsReconciliationResult.UnavailableResult;
            }

            if (current.BoardId != boardId || current.ItemId != mondayItemId ||
                !string.Equals(current.State, "active", StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogWarning(
                    "Hearing details validation failed: TikCounter={TikCounter}, MondayItemId={MondayItemId}, Reason=MondayItemIdentityOrStateInvalid",
                    tikCounter,
                    mondayItemId);
                return HearingDetailsReconciliationResult.FailedResult;
            }

            var sourceStart = ToIsraelLocal(hearing.StartDate!.Value);
            var desiredDate = DateOnly.FromDateTime(sourceStart);
            var desiredTime = TimeOnly.FromDateTime(sourceStart);
            desiredTime = new TimeOnly(desiredTime.Hour, desiredTime.Minute);
            var desiredJudge = string.IsNullOrWhiteSpace(hearing.JudgeName)
                ? null
                : hearing.JudgeName.Trim();

            var dateChanged = current.HearingDate != desiredDate;
            var timeChanged = current.HearingTime != desiredTime;
            var judgeBlocked = desiredJudge == null;
            var judgeChanged = desiredJudge != null &&
                !string.Equals(current.JudgeName, desiredJudge, StringComparison.Ordinal);
            var snapshotDateMissing = snapshot?.NearestStartDateUtc == null;
            var snapshotJudgeMissing = desiredJudge != null && snapshot?.JudgeName == null;
            var dateTimeBaselineChanged = snapshot?.NearestStartDateUtc != sourceStartUtc;
            var judgeBaselineChanged = desiredJudge != null &&
                !string.Equals(snapshot?.JudgeName, desiredJudge, StringComparison.Ordinal);
            var baselineInitialization = !dateChanged && !timeChanged && !judgeChanged &&
                (snapshotDateMissing || snapshotJudgeMissing);
            if (judgeBlocked)
            {
                _logger.LogInformation(
                    "Hearing detail field blocked: TikCounter={TikCounter}, MondayItemId={MondayItemId}, Field=Judge, Reason=SourceValueMissing",
                    tikCounter,
                    mondayItemId);
            }

            var planned = new HearingDetailsReconciliationResult(
                Succeeded: true,
                Unavailable: false,
                DateChangePlanned: dateChanged,
                TimeChangePlanned: timeChanged,
                JudgeChangePlanned: judgeChanged,
                BaselineInitialization: baselineInitialization,
                JudgeBlocked: judgeBlocked,
                PersistDateTime: dateTimeBaselineChanged,
                PersistJudge: judgeBaselineChanged,
                JudgeName: desiredJudge,
                MutationPerformed: false);

            if (!live || (!dateChanged && !timeChanged && !judgeChanged))
            {
                return planned;
            }

            var values = new Dictionary<string, object>();
            if (dateChanged)
            {
                values[dateColumnId] = new { date = desiredDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) };
            }
            if (timeChanged)
            {
                values[hourColumnId] = new { hour = desiredTime.Hour, minute = desiredTime.Minute };
            }
            if (judgeChanged)
            {
                values[judgeColumnId] = desiredJudge!;
            }

            await _mondayClient.UpdateItemAsync(
                boardId,
                mondayItemId,
                JsonSerializer.Serialize(values),
                ct);

            var verified = await _mondayClient.GetHearingDetailsValueAsync(
                boardId,
                mondayItemId,
                dateColumnId,
                hourColumnId,
                judgeColumnId,
                ct);
            if (verified == null ||
                verified.BoardId != boardId ||
                verified.ItemId != mondayItemId ||
                !string.Equals(verified.State, "active", StringComparison.OrdinalIgnoreCase) ||
                verified.HearingDate != desiredDate ||
                verified.HearingTime != desiredTime ||
                (desiredJudge != null && !string.Equals(verified.JudgeName, desiredJudge, StringComparison.Ordinal)))
            {
                _logger.LogWarning(
                    "Hearing details verification failed: TikCounter={TikCounter}, MondayItemId={MondayItemId}, Reason=DetailReadbackMismatch",
                    tikCounter,
                    mondayItemId);
                return planned with
                {
                    Succeeded = false,
                    PersistDateTime = false,
                    PersistJudge = false
                };
            }

            return planned with
            {
                PersistDateTime = dateTimeBaselineChanged,
                PersistJudge = judgeBaselineChanged,
                MutationPerformed = true
            };
        }

        private sealed record HearingDetailsReconciliationResult(
            bool Succeeded,
            bool Unavailable,
            bool DateChangePlanned,
            bool TimeChangePlanned,
            bool JudgeChangePlanned,
            bool BaselineInitialization,
            bool JudgeBlocked,
            bool PersistDateTime,
            bool PersistJudge,
            string? JudgeName,
            bool MutationPerformed)
        {
            public static HearingDetailsReconciliationResult NotApplicable { get; } =
                new(true, false, false, false, false, false, false, false, false, null, false);
            public static HearingDetailsReconciliationResult UnavailableResult { get; } =
                new(false, true, false, false, false, false, false, false, false, null, false);
            public static HearingDetailsReconciliationResult FailedResult { get; } =
                new(false, false, false, false, false, false, false, false, false, null, false);
        }

        private static DateTime ToIsraelLocal(DateTime value)
        {
            if (value.Kind == DateTimeKind.Utc)
            {
                return TimeZoneInfo.ConvertTimeFromUtc(value, IsraelTimeZone);
            }
            if (value.Kind == DateTimeKind.Local)
            {
                return TimeZoneInfo.ConvertTime(value, IsraelTimeZone);
            }
            return value;
        }

        private static DateTime ToSnapshotUtc(DateTime value)
            => value.Kind switch
            {
                DateTimeKind.Utc => value,
                DateTimeKind.Local => value.ToUniversalTime(),
                _ => TimeZoneInfo.ConvertTimeToUtc(value, IsraelTimeZone)
            };

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
            int deliveryUncertain = 0,
            int proposedDateChanges = 0,
            int proposedTimeChanges = 0,
            int proposedJudgeChanges = 0,
            int detailBaselineInitializations = 0,
            int blockedMissingJudge = 0,
            int detailScopeExcluded = 0)
        {
            _logger.LogInformation(
                "HearingNearest reconciliation summary: BoardId={BoardId}, Mode={Mode}, MappedCases={MappedCases}, SelectedActive={SelectedActive}, SelectedCancelledFallback={SelectedCancelledFallback}, SelectedTransferredFallback={SelectedTransferredFallback}, MissingSnapshot={MissingSnapshot}, WouldUpdate={WouldUpdate}, ProposedDateChanges={ProposedDateChanges}, ProposedTimeChanges={ProposedTimeChanges}, ProposedJudgeChanges={ProposedJudgeChanges}, DetailBaselineInitializations={DetailBaselineInitializations}, BlockedMissingJudge={BlockedMissingJudge}, DetailScopeExcluded={DetailScopeExcluded}, SkippedUnavailable={SkippedUnavailable}, ProtectedWorkflowStatus={ProtectedWorkflowStatus}, AdvancedAfterMutation={AdvancedAfterMutation}, LegacyStatusAmbiguous={LegacyStatusAmbiguous}, UnobservedTerminalAmbiguous={UnobservedTerminalAmbiguous}, DeliveryUncertain={DeliveryUncertain}, NoChange={NoChange}, Failed={Failed}",
                boardId,
                mode,
                mappedCases,
                selectedActive,
                selectedCancelledFallback,
                selectedTransferredFallback,
                missingSnapshot,
                wouldUpdate,
                proposedDateChanges,
                proposedTimeChanges,
                proposedJudgeChanges,
                detailBaselineInitializations,
                blockedMissingJudge,
                detailScopeExcluded,
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
