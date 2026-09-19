using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Odmon.Worker.Data;
using Odmon.Worker.Monday;
using Odmon.Worker.Models;
using Odmon.Worker.OdcanitAccess;
using Odmon.Worker.Services;
using Xunit;

namespace Odmon.Worker.Tests
{
    public sealed class HearingNearestSyncServiceTests
    {
        private const long BoardId = 700001;
        private const long MondayItemId = 800001;
        private const int TikCounter = 1001;
        private const int SourceEventId = 1201;
        private const string TikNumber = "SYN-1001";
        private const string CancelledLabel = "מבוטל";
        private const string TransferredLabel = "הועבר";

        [Fact]
        public async Task ExistingMapping_NoSnapshot_FutureCancelled_IsReportedAsLegacyAmbiguous()
        {
            await using var scenario = CreateScenario(meetStatus: 1);

            await scenario.Service.SyncNearestHearingsAsync(BoardId, CancellationToken.None);

            Assert.Empty(scenario.Monday.StatusLabels);
            Assert.Equal(0, scenario.Reader.GetCasesCallCount);
            Assert.Empty(await scenario.Db.HearingNearestSnapshots.ToListAsync());
            Assert.Contains(scenario.Logger.Messages, message =>
                message.Contains("Reason=LegacyStatusAmbiguous", StringComparison.Ordinal));
            Assert.Contains(scenario.Logger.Messages, message =>
                message.Contains("LegacyStatusAmbiguous=1", StringComparison.Ordinal));
        }

        [Fact]
        public async Task ExistingMapping_CreatedBeforeListenerT0_IsStillReconciled()
        {
            var listenerStart = DateTime.UtcNow.AddDays(-10);
            await using var scenario = CreateScenario(
                meetStatus: 1,
                mappingCreatedAtUtc: listenerStart.AddDays(-20),
                listenerStartedAtUtc: listenerStart,
                snapshotMeetStatus: 0);

            await scenario.Service.SyncNearestHearingsAsync(BoardId, CancellationToken.None);

            Assert.Equal(new[] { CancelledLabel }, scenario.Monday.StatusLabels);
            Assert.Single(await scenario.Db.HearingNearestSnapshots.ToListAsync());
        }

        [Fact]
        public async Task FutureTransferredOnly_UpdatesTransferredStatus()
        {
            await using var scenario = CreateScenario(meetStatus: 2, snapshotMeetStatus: 0);

            await scenario.Service.SyncNearestHearingsAsync(BoardId, CancellationToken.None);

            Assert.Equal(new[] { TransferredLabel }, scenario.Monday.StatusLabels);
            Assert.Equal(2, Assert.Single(await scenario.Db.HearingNearestSnapshots.ToListAsync()).NearestMeetStatus);
        }

        [Fact]
        public async Task ActiveHearing_NeverWritesActiveStatus()
        {
            await using var scenario = CreateScenario(meetStatus: 0);

            await scenario.Service.SyncNearestHearingsAsync(BoardId, CancellationToken.None);

            Assert.Empty(scenario.Monday.StatusLabels);
            var snapshot = Assert.Single(await scenario.Db.HearingNearestSnapshots.ToListAsync());
            Assert.Equal(0, snapshot.NearestMeetStatus);
            Assert.Null(snapshot.PendingStatusSourceEventId);
            Assert.Null(snapshot.PendingMeetStatus);
        }

        [Fact]
        public async Task MissingCancellationLabel_DoesNotSaveSnapshot()
        {
            await using var scenario = CreateScenario(
                meetStatus: 1,
                allowedLabels: new[] { TransferredLabel });

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                scenario.Service.SyncNearestHearingsAsync(BoardId, CancellationToken.None));

            Assert.Empty(scenario.Monday.StatusLabels);
            Assert.Empty(await scenario.Db.HearingNearestSnapshots.ToListAsync());
        }

        [Fact]
        public async Task StatusMutationFailure_DoesNotSaveSnapshot()
        {
            await using var scenario = CreateScenario(
                meetStatus: 1,
                failStatusMutation: true,
                snapshotMeetStatus: 0);

            await scenario.Service.SyncNearestHearingsAsync(BoardId, CancellationToken.None);

            var snapshot = Assert.Single(await scenario.Db.HearingNearestSnapshots.ToListAsync());
            Assert.Equal(0, snapshot.NearestMeetStatus);
            Assert.Null(snapshot.DeliveredStatusSourceEventId);
            Assert.Equal(SourceEventId, snapshot.PendingStatusSourceEventId);
            Assert.Equal(1, snapshot.PendingMeetStatus);
            Assert.False(snapshot.PendingInitialStatusWasDesired);
        }

        [Fact]
        public async Task LaterRequiredMutationFailure_DoesNotAdvanceSnapshot()
        {
            await using var scenario = CreateScenario(
                meetStatus: 1,
                includeHearingDetails: true,
                failDateMutation: true,
                hearingMode: HearingNearestMode.Full,
                snapshotMeetStatus: 0);

            await scenario.Service.SyncNearestHearingsAsync(BoardId, CancellationToken.None);

            Assert.Equal(new[] { CancelledLabel }, scenario.Monday.StatusLabels);
            Assert.Equal(0, Assert.Single(await scenario.Db.HearingNearestSnapshots.ToListAsync()).NearestMeetStatus);
        }

        [Fact]
        public async Task DryRun_DoesNotMutateMondayOrSaveSnapshot()
        {
            await using var scenario = CreateScenario(meetStatus: 1, dryRun: true, snapshotMeetStatus: 0);

            await scenario.Service.SyncNearestHearingsAsync(BoardId, CancellationToken.None);

            Assert.Empty(scenario.Monday.StatusLabels);
            var snapshot = Assert.Single(await scenario.Db.HearingNearestSnapshots.ToListAsync());
            Assert.Equal(0, snapshot.NearestMeetStatus);
            Assert.Null(snapshot.PendingStatusSourceEventId);
            Assert.Null(snapshot.PendingMeetStatus);
        }

        [Fact]
        public async Task DisabledMode_IsFailClosedAndDoesNotReadSourceOrMutate()
        {
            await using var scenario = CreateScenario(
                meetStatus: 1,
                hearingMode: HearingNearestMode.Disabled);

            await scenario.Service.SyncNearestHearingsAsync(BoardId, CancellationToken.None);

            Assert.Empty(scenario.Monday.StatusLabels);
            Assert.Equal(0, scenario.Reader.DiaryCallCount);
            Assert.Empty(await scenario.Db.HearingNearestSnapshots.ToListAsync());
        }

        [Fact]
        public async Task RecentlyElapsedCancelled_InsideRecoveryWindow_IsReconciled()
        {
            await using var scenario = CreateScenario(
                meetStatus: 1,
                hearingStartOffset: TimeSpan.FromDays(-2),
                snapshotMeetStatus: 0);

            await scenario.Service.SyncNearestHearingsAsync(BoardId, CancellationToken.None);

            Assert.Equal(new[] { CancelledLabel }, scenario.Monday.StatusLabels);
            Assert.Single(await scenario.Db.HearingNearestSnapshots.ToListAsync());
        }

        [Fact]
        public async Task RecentlyElapsedTransferred_InsideRecoveryWindow_IsReconciled()
        {
            await using var scenario = CreateScenario(
                meetStatus: 2,
                hearingStartOffset: TimeSpan.FromHours(-4),
                snapshotMeetStatus: 0);

            await scenario.Service.SyncNearestHearingsAsync(BoardId, CancellationToken.None);

            Assert.Equal(new[] { TransferredLabel }, scenario.Monday.StatusLabels);
            Assert.Single(await scenario.Db.HearingNearestSnapshots.ToListAsync());
        }

        [Fact]
        public async Task PastActive_IsNotReconciled()
        {
            await using var scenario = CreateScenario(meetStatus: 0, hearingStartOffset: TimeSpan.FromHours(-1));

            await scenario.Service.SyncNearestHearingsAsync(BoardId, CancellationToken.None);

            Assert.Empty(scenario.Monday.StatusLabels);
            Assert.Empty(await scenario.Db.HearingNearestSnapshots.ToListAsync());
        }

        [Fact]
        public async Task ElapsedCancelled_OutsideRecoveryWindow_IsNotReconciled()
        {
            await using var scenario = CreateScenario(meetStatus: 1, hearingStartOffset: TimeSpan.FromDays(-31));

            await scenario.Service.SyncNearestHearingsAsync(BoardId, CancellationToken.None);

            Assert.Empty(scenario.Monday.StatusLabels);
            Assert.Empty(await scenario.Db.HearingNearestSnapshots.ToListAsync());
        }

        [Theory]
        [InlineData(1, TransferredLabel, CancelledLabel)]
        [InlineData(2, CancelledLabel, TransferredLabel)]
        public async Task ManagedStatus_CanFollowGenuineSourceTransition(
            int meetStatus,
            string currentLabel,
            string expectedLabel)
        {
            await using var scenario = CreateScenario(
                meetStatus,
                currentStatusLabel: currentLabel,
                snapshotMeetStatus: meetStatus == 1 ? 2 : 1);

            await scenario.Service.SyncNearestHearingsAsync(BoardId, CancellationToken.None);

            Assert.Equal(new[] { expectedLabel }, scenario.Monday.StatusLabels);
            Assert.Equal(meetStatus, Assert.Single(await scenario.Db.HearingNearestSnapshots.ToListAsync()).NearestMeetStatus);
        }

        [Theory]
        [InlineData(1, HearingStatusWorkflowService.ActiveLabel, CancelledLabel)]
        [InlineData(1, HearingStatusWorkflowService.ActiveBaselineLabel, CancelledLabel)]
        [InlineData(2, HearingStatusWorkflowService.ActiveLabel, TransferredLabel)]
        [InlineData(2, HearingStatusWorkflowService.ActiveBaselineLabel, TransferredLabel)]
        public async Task ActiveManagedBaseline_AllowsNewCancellationOrTransfer(
            int meetStatus,
            string currentLabel,
            string expectedLabel)
        {
            await using var scenario = CreateScenario(
                meetStatus,
                currentStatusLabel: currentLabel,
                snapshotMeetStatus: 0);

            await scenario.Service.SyncNearestHearingsAsync(BoardId, CancellationToken.None);

            Assert.Equal(new[] { expectedLabel }, scenario.Monday.StatusLabels);
            var snapshot = Assert.Single(await scenario.Db.HearingNearestSnapshots.ToListAsync());
            Assert.Equal(SourceEventId, snapshot.DeliveredStatusSourceEventId);
            Assert.Equal(meetStatus, snapshot.DeliveredMeetStatus);
        }

        [Fact]
        public async Task DeliveredCancellation_WithDownstreamLabel_IsNotRepublished()
        {
            await using var scenario = CreateScenario(
                meetStatus: 1,
                currentStatusLabel: "Synthetic downstream workflow",
                snapshotMeetStatus: 1,
                deliveredStatusSourceEventId: SourceEventId,
                deliveredMeetStatus: 1);

            await scenario.Service.SyncNearestHearingsAsync(BoardId, CancellationToken.None);

            Assert.Empty(scenario.Monday.StatusLabels);
            var snapshot = Assert.Single(await scenario.Db.HearingNearestSnapshots.ToListAsync());
            Assert.Equal(SourceEventId, snapshot.DeliveredStatusSourceEventId);
            Assert.Equal(1, snapshot.DeliveredMeetStatus);
        }

        [Fact]
        public async Task NewActiveEvent_IsObservedThenItsCancellationIsDeliveredExactlyOnce()
        {
            const int previousEventId = 1200;
            await using var scenario = CreateScenario(
                meetStatus: 0,
                currentStatusLabel: "Synthetic downstream workflow",
                snapshotMeetStatus: 1,
                deliveredStatusSourceEventId: previousEventId,
                deliveredMeetStatus: 1,
                sourceEventId: SourceEventId,
                observedSourceEventId: previousEventId);

            await scenario.Service.SyncNearestHearingsAsync(BoardId, CancellationToken.None);

            Assert.Empty(scenario.Monday.StatusLabels);
            var afterObservation = Assert.Single(await scenario.Db.HearingNearestSnapshots.ToListAsync());
            Assert.Equal(SourceEventId, afterObservation.ObservedSourceEventId);
            Assert.Equal(0, afterObservation.NearestMeetStatus);
            Assert.Equal(previousEventId, afterObservation.DeliveredStatusSourceEventId);

            scenario.Reader.Hearings[0].MeetStatus = 1;
            await scenario.Service.SyncNearestHearingsAsync(BoardId, CancellationToken.None);

            Assert.Equal(new[] { CancelledLabel }, scenario.Monday.StatusLabels);
            var afterDelivery = Assert.Single(await scenario.Db.HearingNearestSnapshots.ToListAsync());
            Assert.Equal(SourceEventId, afterDelivery.DeliveredStatusSourceEventId);
            Assert.Equal(1, afterDelivery.DeliveredMeetStatus);

            scenario.Monday.CurrentStatusLabel = "Synthetic downstream workflow";
            await scenario.Service.SyncNearestHearingsAsync(BoardId, CancellationToken.None);

            Assert.Single(scenario.Monday.StatusLabels);
        }

        [Theory]
        [InlineData(1)]
        [InlineData(2)]
        public async Task EventFirstObservedTerminal_AfterSuccessfulActiveCycle_IsQuarantinedAsAmbiguous(
            int terminalStatus)
        {
            await using var scenario = CreateScenario(
                meetStatus: 0,
                currentStatusLabel: HearingStatusWorkflowService.ActiveBaselineLabel);

            await scenario.Service.SyncNearestHearingsAsync(BoardId, CancellationToken.None);
            Assert.Equal(SourceEventId, Assert.Single(await scenario.Db.HearingNearestSnapshots.ToListAsync()).ObservedSourceEventId);

            var nextEvent = scenario.Reader.Hearings[0];
            nextEvent.SourceEventId = SourceEventId + 1;
            nextEvent.MeetStatus = terminalStatus;
            await scenario.Service.SyncNearestHearingsAsync(BoardId, CancellationToken.None);

            Assert.Empty(scenario.Monday.StatusLabels);
            var snapshot = Assert.Single(await scenario.Db.HearingNearestSnapshots.ToListAsync());
            Assert.Equal(SourceEventId, snapshot.ObservedSourceEventId);
            Assert.Null(snapshot.DeliveredStatusSourceEventId);
            Assert.Contains(scenario.Logger.Messages, message =>
                message.Contains("Reason=UnobservedTerminalEventAmbiguous", StringComparison.Ordinal));
            Assert.Contains(scenario.Logger.Messages, message =>
                message.Contains("UnobservedTerminalAmbiguous=1", StringComparison.Ordinal));
        }

        [Fact]
        public async Task TrackedCancellation_IsDeliveredEvenWhenSelectorPrefersAnotherFutureActiveEvent()
        {
            var israelTime = TimeZoneInfo.FindSystemTimeZoneById("Israel Standard Time");
            var future = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, israelTime).AddDays(3);
            await using var scenario = CreateScenario(
                meetStatus: 1,
                currentStatusLabel: "Synthetic downstream workflow",
                snapshotMeetStatus: 0,
                additionalHearing: new OdcanitDiaryEvent
                {
                    SourceEventId = SourceEventId + 1,
                    TikCounter = TikCounter,
                    StartDate = future,
                    MeetStatus = 0
                });

            await scenario.Service.SyncNearestHearingsAsync(BoardId, CancellationToken.None);

            Assert.Equal(new[] { CancelledLabel }, scenario.Monday.StatusLabels);
            var snapshot = Assert.Single(await scenario.Db.HearingNearestSnapshots.ToListAsync());
            Assert.Equal(SourceEventId + 1, snapshot.ObservedSourceEventId);
            Assert.Equal(0, snapshot.NearestMeetStatus);
            Assert.Equal(SourceEventId, snapshot.DeliveredStatusSourceEventId);
            Assert.Equal(1, snapshot.DeliveredMeetStatus);
        }

        [Fact]
        public async Task SameManagedLabelFromOlderEvent_DoesNotBlockNewDelivery()
        {
            await using var scenario = CreateScenario(
                meetStatus: 1,
                currentStatusLabel: CancelledLabel,
                snapshotMeetStatus: 0);

            await scenario.Service.SyncNearestHearingsAsync(BoardId, CancellationToken.None);

            Assert.Equal(new[] { CancelledLabel }, scenario.Monday.StatusLabels);
            var snapshot = Assert.Single(await scenario.Db.HearingNearestSnapshots.ToListAsync());
            Assert.Equal(SourceEventId, snapshot.DeliveredStatusSourceEventId);
            Assert.Equal(1, snapshot.DeliveredMeetStatus);
        }

        [Fact]
        public async Task ExactDesiredStatus_ForAlreadyDeliveredEvent_IsNoOp()
        {
            await using var scenario = CreateScenario(
                meetStatus: 1,
                currentStatusLabel: CancelledLabel,
                snapshotMeetStatus: 1,
                deliveredStatusSourceEventId: SourceEventId,
                deliveredMeetStatus: 1);

            await scenario.Service.SyncNearestHearingsAsync(BoardId, CancellationToken.None);

            Assert.Empty(scenario.Monday.StatusLabels);
        }

        [Theory]
        [InlineData("נהג קיבל - דיון בוטל")]
        [InlineData("Synthetic downstream workflow")]
        public async Task ProtectedWorkflowStatus_IsNeverOverwritten(string protectedLabel)
        {
            await using var scenario = CreateScenario(
                meetStatus: 1,
                currentStatusLabel: protectedLabel,
                snapshotMeetStatus: 1,
                deliveredStatusSourceEventId: SourceEventId,
                deliveredMeetStatus: 1);

            await scenario.Service.SyncNearestHearingsAsync(BoardId, CancellationToken.None);

            Assert.Empty(scenario.Monday.StatusLabels);
            var snapshot = Assert.Single(await scenario.Db.HearingNearestSnapshots.ToListAsync());
            Assert.Equal(1, snapshot.NearestMeetStatus);
            Assert.Null(snapshot.NearestStartDateUtc);
            Assert.Null(snapshot.JudgeName);
            Assert.Null(snapshot.City);
        }

        [Theory]
        [InlineData(1, CancelledLabel)]
        [InlineData(2, TransferredLabel)]
        public async Task ConfirmedMutation_AdvancedByBringUp_DeliversEachObservedEventOnce(
            int terminalStatus,
            string expectedLabel)
        {
            await using var scenario = CreateScenario(
                meetStatus: 0,
                currentStatusLabel: HearingStatusWorkflowService.ActiveBaselineLabel,
                statusAfterMutation: "Synthetic advanced workflow");

            // First successful cycle observes active event A without resetting Monday.
            await scenario.Service.SyncNearestHearingsAsync(BoardId, CancellationToken.None);
            Assert.Empty(scenario.Monday.StatusLabels);
            var snapshot = Assert.Single(await scenario.Db.HearingNearestSnapshots.ToListAsync());
            Assert.Equal(SourceEventId, snapshot.ObservedSourceEventId);
            Assert.Equal(0, snapshot.NearestMeetStatus);

            // A becomes terminal. Monday acknowledges the mutation, then BringUp
            // advances the workflow label before our read-back.
            scenario.Reader.Hearings[0].MeetStatus = terminalStatus;
            await scenario.Service.SyncNearestHearingsAsync(BoardId, CancellationToken.None);

            Assert.Equal(new[] { expectedLabel }, scenario.Monday.StatusLabels);
            snapshot = Assert.Single(await scenario.Db.HearingNearestSnapshots.ToListAsync());
            Assert.Equal(SourceEventId, snapshot.DeliveredStatusSourceEventId);
            Assert.Equal(terminalStatus, snapshot.DeliveredMeetStatus);
            Assert.Null(snapshot.PendingStatusSourceEventId);

            // Repeated processing of A is suppressed by the delivered tuple.
            await scenario.Service.SyncNearestHearingsAsync(BoardId, CancellationToken.None);
            Assert.Single(scenario.Monday.StatusLabels);

            // Event B is observed active while BringUp's downstream label remains.
            scenario.Reader.Hearings[0].SourceEventId = SourceEventId + 1;
            scenario.Reader.Hearings[0].MeetStatus = 0;
            await scenario.Service.SyncNearestHearingsAsync(BoardId, CancellationToken.None);
            snapshot = Assert.Single(await scenario.Db.HearingNearestSnapshots.ToListAsync());
            Assert.Equal(SourceEventId + 1, snapshot.ObservedSourceEventId);
            Assert.Equal(0, snapshot.NearestMeetStatus);
            Assert.Single(scenario.Monday.StatusLabels);

            // B then reaches the same terminal status and is published once.
            scenario.Reader.Hearings[0].MeetStatus = terminalStatus;
            await scenario.Service.SyncNearestHearingsAsync(BoardId, CancellationToken.None);
            Assert.Equal(new[] { expectedLabel, expectedLabel }, scenario.Monday.StatusLabels);
            snapshot = Assert.Single(await scenario.Db.HearingNearestSnapshots.ToListAsync());
            Assert.Equal(SourceEventId + 1, snapshot.DeliveredStatusSourceEventId);
            Assert.Equal(terminalStatus, snapshot.DeliveredMeetStatus);
            Assert.Null(snapshot.PendingStatusSourceEventId);

            await scenario.Service.SyncNearestHearingsAsync(BoardId, CancellationToken.None);
            Assert.Equal(2, scenario.Monday.StatusLabels.Count);
        }

        [Fact]
        public async Task TimeoutAfterMondayAcceptedMutation_IsRecoveredByExactReadWithoutRepeating()
        {
            await using var scenario = CreateScenario(
                meetStatus: 1,
                currentStatusLabel: HearingStatusWorkflowService.ActiveBaselineLabel,
                snapshotMeetStatus: 0,
                failStatusMutationAfterApply: true);

            await scenario.Service.SyncNearestHearingsAsync(BoardId, CancellationToken.None);

            Assert.Single(scenario.Monday.StatusLabels);
            var pending = Assert.Single(await scenario.Db.HearingNearestSnapshots.ToListAsync());
            Assert.Equal(SourceEventId, pending.PendingStatusSourceEventId);
            Assert.False(pending.PendingInitialStatusWasDesired);
            Assert.Null(pending.DeliveredStatusSourceEventId);

            scenario.Monday.FailStatusMutationAfterApply = false;
            await scenario.Service.SyncNearestHearingsAsync(BoardId, CancellationToken.None);

            Assert.Single(scenario.Monday.StatusLabels);
            var recovered = Assert.Single(await scenario.Db.HearingNearestSnapshots.ToListAsync());
            Assert.Equal(SourceEventId, recovered.DeliveredStatusSourceEventId);
            Assert.Equal(1, recovered.DeliveredMeetStatus);
            Assert.Null(recovered.PendingStatusSourceEventId);
        }

        [Fact]
        public async Task OlderUncertainAttempt_WithProtectedLabel_IsNotRepeatedOrMarkedDelivered()
        {
            await using var scenario = CreateScenario(
                meetStatus: 1,
                currentStatusLabel: "Synthetic downstream workflow",
                snapshotMeetStatus: 0,
                pendingStatusSourceEventId: SourceEventId,
                pendingMeetStatus: 1,
                pendingInitialStatusWasDesired: false);

            await scenario.Service.SyncNearestHearingsAsync(BoardId, CancellationToken.None);

            Assert.Empty(scenario.Monday.StatusLabels);
            var snapshot = Assert.Single(await scenario.Db.HearingNearestSnapshots.ToListAsync());
            Assert.Null(snapshot.DeliveredStatusSourceEventId);
            Assert.Equal(SourceEventId, snapshot.PendingStatusSourceEventId);
            Assert.Contains(scenario.Logger.Messages, message =>
                message.Contains("Reason=PendingDeliveryProtectedWorkflowState", StringComparison.Ordinal));
        }

        [Fact]
        public async Task CrashAfterMutationBeforeDeliveryPersistence_IsRecoveredWithoutRepeating()
        {
            await using var scenario = CreateScenario(
                meetStatus: 1,
                currentStatusLabel: CancelledLabel,
                snapshotMeetStatus: 0,
                pendingStatusSourceEventId: SourceEventId,
                pendingMeetStatus: 1,
                pendingInitialStatusWasDesired: false);

            await scenario.Service.SyncNearestHearingsAsync(BoardId, CancellationToken.None);

            Assert.Empty(scenario.Monday.StatusLabels);
            var snapshot = Assert.Single(await scenario.Db.HearingNearestSnapshots.ToListAsync());
            Assert.Equal(SourceEventId, snapshot.DeliveredStatusSourceEventId);
            Assert.Equal(1, snapshot.DeliveredMeetStatus);
            Assert.Null(snapshot.PendingStatusSourceEventId);
        }

        [Fact]
        public async Task CrashBeforeMutation_WhenDesiredLabelPreexisted_RemainsUncertainWithoutRetry()
        {
            await using var scenario = CreateScenario(
                meetStatus: 1,
                currentStatusLabel: CancelledLabel,
                snapshotMeetStatus: 0,
                pendingStatusSourceEventId: SourceEventId,
                pendingMeetStatus: 1,
                pendingInitialStatusWasDesired: true);

            await scenario.Service.SyncNearestHearingsAsync(BoardId, CancellationToken.None);

            Assert.Empty(scenario.Monday.StatusLabels);
            var snapshot = Assert.Single(await scenario.Db.HearingNearestSnapshots.ToListAsync());
            Assert.Null(snapshot.DeliveredStatusSourceEventId);
            Assert.Equal(SourceEventId, snapshot.PendingStatusSourceEventId);
            Assert.Contains(scenario.Logger.Messages, message =>
                message.Contains("Reason=PendingExactStatePreexisted", StringComparison.Ordinal));
        }

        [Fact]
        public async Task StatusOnlyMode_MissingSnapshotCannotMutateOtherHearingFields()
        {
            await using var scenario = CreateScenario(
                meetStatus: 1,
                includeHearingDetails: true,
                hearingMode: HearingNearestMode.StatusOnly,
                snapshotMeetStatus: 0);

            await scenario.Service.SyncNearestHearingsAsync(BoardId, CancellationToken.None);

            Assert.Equal(new[] { CancelledLabel }, scenario.Monday.StatusLabels);
            Assert.Equal(0, scenario.Monday.DetailsMutationCount);
            Assert.Equal(0, scenario.Monday.DateMutationCount);
            var snapshot = Assert.Single(await scenario.Db.HearingNearestSnapshots.ToListAsync());
            Assert.Null(snapshot.NearestStartDateUtc);
            Assert.Null(snapshot.JudgeName);
            Assert.Null(snapshot.City);
        }

        [Theory]
        [InlineData("archived")]
        [InlineData("inactive")]
        public async Task ReturnedUnavailableMondayItem_IsSkippedWithoutFailureMutationOrSnapshot(string itemState)
        {
            await using var scenario = CreateScenario(
                meetStatus: 1,
                mondayItemState: itemState,
                snapshotMeetStatus: 0);

            await scenario.Service.SyncNearestHearingsAsync(BoardId, CancellationToken.None);

            Assert.Empty(scenario.Monday.StatusLabels);
            Assert.Equal(0, scenario.Monday.DetailsMutationCount);
            Assert.Equal(0, scenario.Monday.DateMutationCount);
            AssertUnadvancedSeedSnapshot(await scenario.Db.HearingNearestSnapshots.SingleAsync());
            Assert.Contains(scenario.Logger.Messages, message =>
                message.Contains("Reason=MondayItemUnavailable", StringComparison.Ordinal));
            Assert.Contains(scenario.Logger.Messages, message =>
                message.Contains("SkippedUnavailable=1", StringComparison.Ordinal) &&
                message.Contains("Failed=0", StringComparison.Ordinal));
        }

        [Fact]
        public async Task EmptySuccessfulItemResult_IsSkippedUnavailable()
        {
            await using var scenario = CreateScenario(
                meetStatus: 1,
                mondayItemMissing: true,
                snapshotMeetStatus: 0);

            await scenario.Service.SyncNearestHearingsAsync(BoardId, CancellationToken.None);

            Assert.Empty(scenario.Monday.StatusLabels);
            AssertUnadvancedSeedSnapshot(await scenario.Db.HearingNearestSnapshots.SingleAsync());
            Assert.Contains(scenario.Logger.Messages, message =>
                message.Contains("Reason=MondayItemUnavailable", StringComparison.Ordinal));
            Assert.Contains(scenario.Logger.Messages, message =>
                message.Contains("SkippedUnavailable=1", StringComparison.Ordinal) &&
                message.Contains("Failed=0", StringComparison.Ordinal));
        }

        [Fact]
        public async Task DeletedMondayItem_RemainsValidationFailure()
        {
            await using var scenario = CreateScenario(
                meetStatus: 1,
                mondayItemState: "deleted",
                snapshotMeetStatus: 0);

            await scenario.Service.SyncNearestHearingsAsync(BoardId, CancellationToken.None);

            Assert.Empty(scenario.Monday.StatusLabels);
            AssertUnadvancedSeedSnapshot(await scenario.Db.HearingNearestSnapshots.SingleAsync());
            Assert.Contains(scenario.Logger.Messages, message =>
                message.Contains("Reason=MONDAY_ITEM_INVALID_STATE", StringComparison.Ordinal));
            Assert.Contains(scenario.Logger.Messages, message =>
                message.Contains("SkippedUnavailable=0", StringComparison.Ordinal) &&
                message.Contains("Failed=1", StringComparison.Ordinal));
        }

        [Theory]
        [InlineData(true, false)]
        [InlineData(false, true)]
        public async Task WrongMondayIdentity_RemainsValidationFailure(
            bool wrongBoard,
            bool wrongItem)
        {
            await using var scenario = CreateScenario(
                meetStatus: 1,
                returnedBoardId: wrongBoard ? BoardId + 1 : null,
                returnedItemId: wrongItem ? MondayItemId + 1 : null,
                snapshotMeetStatus: 0);

            await scenario.Service.SyncNearestHearingsAsync(BoardId, CancellationToken.None);

            Assert.Empty(scenario.Monday.StatusLabels);
            AssertUnadvancedSeedSnapshot(await scenario.Db.HearingNearestSnapshots.SingleAsync());
            Assert.Contains(scenario.Logger.Messages, message =>
                message.Contains("Reason=MONDAY_ITEM_IDENTITY_MISMATCH", StringComparison.Ordinal));
            Assert.Contains(scenario.Logger.Messages, message =>
                message.Contains("SkippedUnavailable=0", StringComparison.Ordinal) &&
                message.Contains("Failed=1", StringComparison.Ordinal));
        }

        [Fact]
        public async Task MondayApiFailure_RemainsValidationFailure()
        {
            await using var scenario = CreateScenario(
                meetStatus: 1,
                statusReadException: new MondayApiException(
                    "Synthetic GraphQL failure.",
                    errorCode: "SYNTHETIC_GRAPHQL_ERROR"),
                snapshotMeetStatus: 0);

            await scenario.Service.SyncNearestHearingsAsync(BoardId, CancellationToken.None);

            Assert.Empty(scenario.Monday.StatusLabels);
            AssertUnadvancedSeedSnapshot(await scenario.Db.HearingNearestSnapshots.SingleAsync());
            Assert.Contains(scenario.Logger.Messages, message =>
                message.Contains("Reason=MONDAY_SYNTHETIC_GRAPHQL_ERROR", StringComparison.Ordinal));
            Assert.Contains(scenario.Logger.Messages, message =>
                message.Contains("SkippedUnavailable=0", StringComparison.Ordinal) &&
                message.Contains("Failed=1", StringComparison.Ordinal));
        }

        [Theory]
        [InlineData(1, CancelledLabel)]
        [InlineData(2, TransferredLabel)]
        public async Task SourceStatusChange_WithUnavailablePeer_UpdatesOnlyStatusThenIsIdempotent(
            int newMeetStatus,
            string expectedLabel)
        {
            await using var scenario = CreateMixedStatusChangeScenario(newMeetStatus);

            await scenario.Service.SyncNearestHearingsAsync(BoardId, CancellationToken.None);

            var mutation = Assert.Single(scenario.Monday.StatusMutations);
            Assert.Equal(BoardId, mutation.BoardId);
            Assert.Equal(scenario.ActiveItemId, mutation.ItemId);
            Assert.Equal(expectedLabel, mutation.Label);
            Assert.Equal("synthetic_status", mutation.ColumnId);
            Assert.Equal(0, scenario.Monday.DetailsMutationCount);
            Assert.Equal(0, scenario.Monday.DateMutationCount);

            var activeSnapshot = await scenario.Db.HearingNearestSnapshots.SingleAsync(
                item => item.MondayItemId == scenario.ActiveItemId);
            Assert.Equal(newMeetStatus, activeSnapshot.NearestMeetStatus);
            Assert.Equal(1301, activeSnapshot.ObservedSourceEventId);
            Assert.Equal(1301, activeSnapshot.DeliveredStatusSourceEventId);
            Assert.Equal(newMeetStatus, activeSnapshot.DeliveredMeetStatus);
            Assert.Equal(scenario.OriginalStartDateUtc, activeSnapshot.NearestStartDateUtc);
            Assert.Equal("Synthetic existing judge", activeSnapshot.JudgeName);
            Assert.Equal("Synthetic existing city", activeSnapshot.City);

            var unavailableSnapshot = await scenario.Db.HearingNearestSnapshots.SingleAsync(
                item => item.MondayItemId == scenario.UnavailableItemId);
            Assert.Equal(0, unavailableSnapshot.NearestMeetStatus);
            Assert.Equal(scenario.UnavailableLastSyncedAtUtc, unavailableSnapshot.LastSyncedAtUtc);
            var activeLastSyncedAfterFirstRun = activeSnapshot.LastSyncedAtUtc;

            await scenario.Service.SyncNearestHearingsAsync(BoardId, CancellationToken.None);

            Assert.Single(scenario.Monday.StatusMutations);
            Assert.Equal(
                activeLastSyncedAfterFirstRun,
                (await scenario.Db.HearingNearestSnapshots.SingleAsync(
                    item => item.MondayItemId == scenario.ActiveItemId)).LastSyncedAtUtc);
            Assert.Equal(
                scenario.UnavailableLastSyncedAtUtc,
                (await scenario.Db.HearingNearestSnapshots.SingleAsync(
                    item => item.MondayItemId == scenario.UnavailableItemId)).LastSyncedAtUtc);
        }

        private static void AssertUnadvancedSeedSnapshot(HearingNearestSnapshot snapshot)
        {
            Assert.Equal(SourceEventId, snapshot.ObservedSourceEventId);
            Assert.Equal(0, snapshot.NearestMeetStatus);
            Assert.Null(snapshot.DeliveredStatusSourceEventId);
            Assert.Null(snapshot.DeliveredMeetStatus);
        }

        private static MixedScenario CreateMixedStatusChangeScenario(int newMeetStatus)
        {
            const long activeItemId = 810001;
            const long unavailableItemId = 810002;
            const int activeTikCounter = 1101;
            const int unavailableTikCounter = 1102;
            const int activeSourceEventId = 1301;
            const int unavailableSourceEventId = 1302;
            const string activeTikNumber = "SYN-1101";
            const string unavailableTikNumber = "SYN-1102";
            var unavailableLastSyncedAtUtc = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);
            var originalStartDateUtc = new DateTime(2026, 1, 3, 4, 5, 6, DateTimeKind.Utc);

            var options = new DbContextOptionsBuilder<IntegrationDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
                .Options;
            var db = new IntegrationDbContext(options);
            db.MondayItemMappings.AddRange(
                new MondayItemMapping
                {
                    TikCounter = activeTikCounter,
                    TikNumber = activeTikNumber,
                    MondayItemId = activeItemId,
                    BoardId = BoardId,
                    CreatedAtUtc = DateTime.UnixEpoch
                },
                new MondayItemMapping
                {
                    TikCounter = unavailableTikCounter,
                    TikNumber = unavailableTikNumber,
                    MondayItemId = unavailableItemId,
                    BoardId = BoardId,
                    CreatedAtUtc = DateTime.UnixEpoch
                });
            db.HearingNearestSnapshots.AddRange(
                new HearingNearestSnapshot
                {
                    TikCounter = activeTikCounter,
                    BoardId = BoardId,
                    MondayItemId = activeItemId,
                    ObservedSourceEventId = activeSourceEventId,
                    NearestMeetStatus = 0,
                    NearestStartDateUtc = originalStartDateUtc,
                    JudgeName = "Synthetic existing judge",
                    City = "Synthetic existing city",
                    LastSyncedAtUtc = DateTime.UnixEpoch
                },
                new HearingNearestSnapshot
                {
                    TikCounter = unavailableTikCounter,
                    BoardId = BoardId,
                    MondayItemId = unavailableItemId,
                    ObservedSourceEventId = unavailableSourceEventId,
                    NearestMeetStatus = 0,
                    LastSyncedAtUtc = unavailableLastSyncedAtUtc
                });
            db.SaveChanges();

            var israelTime = TimeZoneInfo.FindSystemTimeZoneById("Israel Standard Time");
            var futureStart = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, israelTime).AddDays(2);
            var reader = new MixedFakeOdcanitReader(
                new Dictionary<string, int>(StringComparer.Ordinal)
                {
                    [activeTikNumber] = activeTikCounter,
                    [unavailableTikNumber] = unavailableTikCounter
                },
                [
                    new OdcanitDiaryEvent
                    {
                        SourceEventId = activeSourceEventId,
                        TikCounter = activeTikCounter,
                        StartDate = futureStart,
                        MeetStatus = newMeetStatus,
                        JudgeName = "Synthetic new judge",
                        City = "Synthetic new city"
                    },
                    new OdcanitDiaryEvent
                    {
                        SourceEventId = unavailableSourceEventId,
                        TikCounter = unavailableTikCounter,
                        StartDate = futureStart.AddHours(1),
                        MeetStatus = newMeetStatus
                    }
                ]);
            var monday = new FakeMondayClient();
            monday.ItemStatusValues[activeItemId] = new MondayItemStatusValue(
                BoardId,
                activeItemId,
                "active",
                HearingStatusWorkflowService.ActiveBaselineLabel);
            monday.ItemStatusValues[unavailableItemId] = null;
            var metadata = new FakeMetadataProvider(
            [
                HearingStatusWorkflowService.ActiveBaselineLabel,
                HearingStatusWorkflowService.ActiveLabel,
                CancelledLabel,
                TransferredLabel
            ]);
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Testing:Enable"] = "false",
                    ["OdcanitWrites:Enable"] = "true",
                    ["OdcanitWrites:DryRun"] = "false",
                    ["HearingNearest:Mode"] = HearingNearestMode.StatusOnly.ToString(),
                    ["HearingNearest:DryRun"] = "false",
                    ["HearingNearest:RecoveryLookbackDays"] = "30"
                })
                .Build();
            var settings = Options.Create(new MondaySettings
            {
                HearingStatusColumnId = "synthetic_status",
                JudgeNameColumnId = "synthetic_judge",
                HearingDateColumnId = "synthetic_date",
                HearingHourColumnId = "synthetic_hour"
            });
            var mappingReader = new MondayMappingReadService(
                db,
                NullLogger<MondayMappingReadService>.Instance);
            var service = new HearingNearestSyncService(
                reader,
                db,
                monday,
                metadata,
                configuration,
                settings,
                new RecordingLogger<HearingNearestSyncService>(),
                new FakeSkipLogger(),
                mappingReader,
                new HearingStatusWorkflowService(monday, new FakeDelay()));

            return new MixedScenario(
                db,
                service,
                monday,
                activeItemId,
                unavailableItemId,
                originalStartDateUtc,
                unavailableLastSyncedAtUtc);
        }

        private static Scenario CreateScenario(
            int meetStatus,
            DateTime? mappingCreatedAtUtc = null,
            DateTime? listenerStartedAtUtc = null,
            IEnumerable<string>? allowedLabels = null,
            bool failStatusMutation = false,
            bool failStatusMutationAfterApply = false,
            TimeSpan? hearingStartOffset = null,
            bool includeHearingDetails = false,
            bool failDateMutation = false,
            bool dryRun = false,
            HearingNearestMode hearingMode = HearingNearestMode.StatusOnly,
            string? currentStatusLabel = null,
            string? statusAfterMutation = null,
            int? snapshotMeetStatus = null,
            int? deliveredStatusSourceEventId = null,
            int? deliveredMeetStatus = null,
            int? pendingStatusSourceEventId = null,
            int? pendingMeetStatus = null,
            bool? pendingInitialStatusWasDesired = null,
            int sourceEventId = SourceEventId,
            int? observedSourceEventId = null,
            OdcanitDiaryEvent? additionalHearing = null,
            string mondayItemState = "active",
            bool mondayItemMissing = false,
            long? returnedBoardId = null,
            long? returnedItemId = null,
            Exception? statusReadException = null)
        {
            var options = new DbContextOptionsBuilder<IntegrationDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
                .Options;
            var db = new IntegrationDbContext(options);
            db.MondayItemMappings.Add(new MondayItemMapping
            {
                TikCounter = TikCounter,
                TikNumber = TikNumber,
                MondayItemId = MondayItemId,
                BoardId = BoardId,
                CreatedAtUtc = mappingCreatedAtUtc ?? DateTime.UtcNow.AddDays(-1)
            });
            if (snapshotMeetStatus.HasValue)
            {
                db.HearingNearestSnapshots.Add(new HearingNearestSnapshot
                {
                    TikCounter = TikCounter,
                    BoardId = BoardId,
                    MondayItemId = MondayItemId,
                    ObservedSourceEventId = observedSourceEventId ?? sourceEventId,
                    NearestMeetStatus = snapshotMeetStatus.Value,
                    DeliveredStatusSourceEventId = deliveredStatusSourceEventId,
                    DeliveredMeetStatus = deliveredMeetStatus,
                    PendingStatusSourceEventId = pendingStatusSourceEventId,
                    PendingMeetStatus = pendingMeetStatus,
                    PendingInitialStatusWasDesired = pendingInitialStatusWasDesired,
                    PendingStatusSinceUtc = pendingStatusSourceEventId.HasValue
                        ? DateTime.UtcNow.AddMinutes(-5)
                        : null,
                    LastSyncedAtUtc = DateTime.UtcNow.AddDays(-1)
                });
            }
            if (listenerStartedAtUtc.HasValue)
            {
                db.ListenerStates.Add(new ListenerState
                {
                    Id = 1,
                    StartedAtUtc = listenerStartedAtUtc.Value,
                    UpdatedAtUtc = listenerStartedAtUtc.Value
                });
            }
            db.SaveChanges();

            var israelTime = TimeZoneInfo.FindSystemTimeZoneById("Israel Standard Time");
            var hearingStartLocal = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, israelTime)
                .Add(hearingStartOffset ?? TimeSpan.FromDays(2));
            var hearings = new List<OdcanitDiaryEvent>
            {
                new()
                {
                    SourceEventId = sourceEventId,
                    TikCounter = TikCounter,
                    StartDate = hearingStartLocal,
                    MeetStatus = meetStatus,
                    JudgeName = includeHearingDetails ? "Synthetic Judge" : null,
                    City = includeHearingDetails ? "Synthetic City" : null
                }
            };
            if (additionalHearing != null)
            {
                hearings.Add(additionalHearing);
            }
            var reader = new FakeOdcanitReader(hearings);
            var monday = new FakeMondayClient
            {
                FailStatusMutation = failStatusMutation,
                FailStatusMutationAfterApply = failStatusMutationAfterApply,
                FailDateMutation = failDateMutation,
                CurrentStatusLabel = currentStatusLabel,
                StatusAfterMutation = statusAfterMutation,
                ItemState = mondayItemState,
                ItemMissing = mondayItemMissing,
                ReturnedBoardId = returnedBoardId,
                ReturnedItemId = returnedItemId,
                StatusReadException = statusReadException
            };
            var metadata = new FakeMetadataProvider(allowedLabels ?? new[]
            {
                HearingStatusWorkflowService.ActiveBaselineLabel,
                HearingStatusWorkflowService.ActiveLabel,
                CancelledLabel,
                TransferredLabel
            });
            var skipLogger = new FakeSkipLogger();
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Testing:Enable"] = "false",
                    ["OdcanitWrites:Enable"] = "true",
                    ["OdcanitWrites:DryRun"] = "false",
                    ["HearingNearest:Mode"] = hearingMode.ToString(),
                    ["HearingNearest:DryRun"] = dryRun.ToString(),
                    ["HearingNearest:RecoveryLookbackDays"] = "30"
                })
                .Build();
            var mondaySettings = Options.Create(new MondaySettings
            {
                HearingStatusColumnId = "synthetic_status",
                JudgeNameColumnId = "synthetic_judge",
                HearingDateColumnId = "synthetic_date",
                HearingHourColumnId = "synthetic_hour"
            });
            var mappingReader = new MondayMappingReadService(
                db,
                NullLogger<MondayMappingReadService>.Instance);
            var logger = new RecordingLogger<HearingNearestSyncService>();
            var service = new HearingNearestSyncService(
                reader,
                db,
                monday,
                metadata,
                configuration,
                mondaySettings,
                logger,
                skipLogger,
                mappingReader,
                new HearingStatusWorkflowService(monday, new FakeDelay()));

            return new Scenario(db, service, reader, monday, skipLogger, logger);
        }

        private sealed class Scenario(
            IntegrationDbContext db,
            HearingNearestSyncService service,
            FakeOdcanitReader reader,
            FakeMondayClient monday,
            FakeSkipLogger skipLogger,
            RecordingLogger<HearingNearestSyncService> logger) : IAsyncDisposable
        {
            public IntegrationDbContext Db { get; } = db;
            public HearingNearestSyncService Service { get; } = service;
            public FakeOdcanitReader Reader { get; } = reader;
            public FakeMondayClient Monday { get; } = monday;
            public FakeSkipLogger SkipLogger { get; } = skipLogger;
            public RecordingLogger<HearingNearestSyncService> Logger { get; } = logger;

            public ValueTask DisposeAsync() => Db.DisposeAsync();
        }

        private sealed class MixedScenario(
            IntegrationDbContext db,
            HearingNearestSyncService service,
            FakeMondayClient monday,
            long activeItemId,
            long unavailableItemId,
            DateTime originalStartDateUtc,
            DateTime unavailableLastSyncedAtUtc) : IAsyncDisposable
        {
            public IntegrationDbContext Db { get; } = db;
            public HearingNearestSyncService Service { get; } = service;
            public FakeMondayClient Monday { get; } = monday;
            public long ActiveItemId { get; } = activeItemId;
            public long UnavailableItemId { get; } = unavailableItemId;
            public DateTime OriginalStartDateUtc { get; } = originalStartDateUtc;
            public DateTime UnavailableLastSyncedAtUtc { get; } = unavailableLastSyncedAtUtc;
            public ValueTask DisposeAsync() => Db.DisposeAsync();
        }

        private sealed class FakeOdcanitReader(List<OdcanitDiaryEvent> hearings) : IOdcanitReader
        {
            public int GetCasesCallCount { get; private set; }
            public int DiaryCallCount { get; private set; }
            public List<OdcanitDiaryEvent> Hearings { get; } = hearings;

            public Task<List<OdcanitCase>> GetCasesCreatedOnDateAsync(DateTime date, CancellationToken ct)
                => Task.FromResult(new List<OdcanitCase>());

            public Task<List<OdcanitCase>> GetCasesByTikCountersAsync(IEnumerable<int> tikCounters, CancellationToken ct)
            {
                GetCasesCallCount++;
                return Task.FromResult(new List<OdcanitCase>
                {
                    new() { TikCounter = TikCounter, TikNumber = TikNumber, IsReadyForMonday = false }
                });
            }

            public Task<List<OdcanitDiaryEvent>> GetDiaryEventsByTikCountersAsync(
                IEnumerable<int> tikCounters,
                CancellationToken ct)
            {
                DiaryCallCount++;
                return Task.FromResult(Hearings.ToList());
            }

            public Task<Dictionary<string, int>> ResolveTikNumbersToCountersAsync(
                IEnumerable<string> tikNumbers,
                CancellationToken ct)
                => Task.FromResult(new Dictionary<string, int>(StringComparer.Ordinal)
                {
                    [TikNumber] = TikCounter
                });

            public Task<List<int>> GetTikCountersSinceCutoffAsync(DateTime cutoffDate, CancellationToken ct)
                => Task.FromResult(new List<int>());
        }

        private sealed class MixedFakeOdcanitReader(
            IReadOnlyDictionary<string, int> resolutions,
            IReadOnlyList<OdcanitDiaryEvent> hearings) : IOdcanitReader
        {
            public Task<List<OdcanitCase>> GetCasesCreatedOnDateAsync(DateTime date, CancellationToken ct)
                => Task.FromResult(new List<OdcanitCase>());

            public Task<List<OdcanitCase>> GetCasesByTikCountersAsync(
                IEnumerable<int> tikCounters,
                CancellationToken ct)
                => Task.FromResult(new List<OdcanitCase>());

            public Task<List<OdcanitDiaryEvent>> GetDiaryEventsByTikCountersAsync(
                IEnumerable<int> tikCounters,
                CancellationToken ct)
            {
                var requested = tikCounters.ToHashSet();
                return Task.FromResult(hearings
                    .Where(item => item.TikCounter.HasValue && requested.Contains(item.TikCounter.Value))
                    .ToList());
            }

            public Task<Dictionary<string, int>> ResolveTikNumbersToCountersAsync(
                IEnumerable<string> tikNumbers,
                CancellationToken ct)
            {
                var requested = tikNumbers.ToHashSet(StringComparer.Ordinal);
                return Task.FromResult(resolutions
                    .Where(pair => requested.Contains(pair.Key))
                    .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal));
            }

            public Task<List<int>> GetTikCountersSinceCutoffAsync(DateTime cutoffDate, CancellationToken ct)
                => Task.FromResult(new List<int>());
        }

        private sealed class FakeMondayClient : IMondayClient
        {
            public List<string> StatusLabels { get; } = new();
            public List<(long BoardId, long ItemId, string Label, string ColumnId)> StatusMutations { get; } = new();
            public Dictionary<long, MondayItemStatusValue?> ItemStatusValues { get; } = new();
            public bool FailStatusMutation { get; init; }
            public bool FailStatusMutationAfterApply { get; set; }
            public bool FailDateMutation { get; init; }
            public string? CurrentStatusLabel { get; set; }
            public string? StatusAfterMutation { get; init; }
            public int DetailsMutationCount { get; private set; }
            public int DateMutationCount { get; private set; }
            public string ItemState { get; init; } = "active";
            public bool ItemMissing { get; init; }
            public long? ReturnedBoardId { get; init; }
            public long? ReturnedItemId { get; init; }
            public Exception? StatusReadException { get; init; }

            public Task UpdateHearingStatusAsync(long boardId, long itemId, string label, string statusColumnId, CancellationToken ct)
            {
                if (FailStatusMutation)
                {
                    throw new InvalidOperationException("Synthetic Monday failure");
                }
                StatusLabels.Add(label);
                StatusMutations.Add((boardId, itemId, label, statusColumnId));
                var persistedLabel = StatusAfterMutation ?? label;
                if (ItemStatusValues.TryGetValue(itemId, out var value) && value != null)
                {
                    ItemStatusValues[itemId] = value with { Label = persistedLabel };
                }
                else
                {
                    CurrentStatusLabel = persistedLabel;
                }
                if (FailStatusMutationAfterApply)
                {
                    throw new TimeoutException("Synthetic timeout after mutation acceptance");
                }
                return Task.CompletedTask;
            }

            public Task UpdateHearingDetailsAsync(long boardId, long itemId, string judgeName, string city, string judgeColumnId, string cityColumnId, CancellationToken ct)
            {
                DetailsMutationCount++;
                return Task.CompletedTask;
            }
            public Task UpdateHearingDateAsync(long boardId, long itemId, DateTime startDate, string dateColumnId, string hourColumnId, CancellationToken ct)
            {
                DateMutationCount++;
                if (FailDateMutation)
                {
                    throw new InvalidOperationException("Synthetic Monday date failure");
                }
                return Task.CompletedTask;
            }
            public Task<IReadOnlyList<string>> GetBoardGroupIdsAsync(long boardId, CancellationToken ct)
                => Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());
            public Task<long> CreateItemAsync(long boardId, string groupId, string itemName, string columnValuesJson, CancellationToken ct)
                => Task.FromResult(0L);
            public Task<string?> GetItemStateAsync(long boardId, long itemId, CancellationToken ct)
                => Task.FromResult<string?>("active");
            public Task<MondayItemStatusValue?> GetItemStatusValueAsync(long boardId, long itemId, string statusColumnId, CancellationToken ct)
            {
                if (StatusReadException != null)
                {
                    throw StatusReadException;
                }

                if (ItemStatusValues.TryGetValue(itemId, out var configuredValue))
                {
                    return Task.FromResult(configuredValue);
                }

                return Task.FromResult(ItemMissing
                    ? null
                    : new MondayItemStatusValue(
                        ReturnedBoardId ?? boardId,
                        ReturnedItemId ?? itemId,
                        ItemState,
                        CurrentStatusLabel));
            }
            public Task UpdateItemAsync(long boardId, long itemId, string columnValuesJson, CancellationToken ct)
                => Task.CompletedTask;
            public Task UpdateItemNameAsync(long boardId, long itemId, string name, CancellationToken ct)
                => Task.CompletedTask;
            public Task<long?> FindItemIdByColumnValueAsync(long boardId, string columnId, string columnValue, CancellationToken ct)
                => Task.FromResult<long?>(null);
            public Task<string?> GetHearingApprovalStatusAsync(long itemId, CancellationToken ct)
                => Task.FromResult<string?>(null);
        }

        private sealed class FakeMetadataProvider(IEnumerable<string> allowedLabels) : IMondayMetadataProvider
        {
            private readonly HashSet<string> _allowedLabels = new(allowedLabels, StringComparer.Ordinal);

            public Task<HashSet<string>> GetAllowedStatusLabelsAsync(long boardId, string columnId, CancellationToken ct = default)
                => Task.FromResult(new HashSet<string>(_allowedLabels, StringComparer.Ordinal));
            public Task<HashSet<string>> GetAllowedDropdownLabelsAsync(long boardId, string columnId, CancellationToken ct = default)
                => Task.FromResult(new HashSet<string>(StringComparer.Ordinal));
            public Task<string> GetColumnIdByTitleAsync(long boardId, string title, CancellationToken ct = default)
                => Task.FromResult(string.Empty);
            public Task<string?> GetColumnTypeAsync(long boardId, string columnId, CancellationToken ct = default)
                => Task.FromResult<string?>(null);
            public Task<Dictionary<string, BoardColumnMetadata>> GetBoardColumnsMetadataAsync(long boardId, CancellationToken ct = default)
                => Task.FromResult(new Dictionary<string, BoardColumnMetadata>(StringComparer.Ordinal)
                {
                    ["synthetic_status"] = new()
                    {
                        ColumnId = "synthetic_status",
                        ColumnType = "color"
                    }
                });
        }

        private sealed class FakeDelay : IHearingStatusDelay
        {
            public Task DelayAsync(TimeSpan delay, CancellationToken ct) => Task.CompletedTask;
        }

        private sealed class FakeSkipLogger : ISkipLogger
        {
            public int CallCount { get; private set; }

            public Task LogSkipAsync(
                int tikCounter,
                string? tikNumber,
                string operation,
                string reasonCode,
                string? entityId,
                string? rawValue,
                object? details,
                CancellationToken ct)
            {
                CallCount++;
                return Task.CompletedTask;
            }
        }

        private sealed class RecordingLogger<T> : ILogger<T>
        {
            public List<string> Messages { get; } = new();

            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter)
                => Messages.Add(formatter(state, exception));
        }
    }
}
