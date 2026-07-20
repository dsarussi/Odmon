using System;
using System.Collections.Generic;
using System.Linq;
using Odmon.Worker.Models;
using Odmon.Worker.Services;
using Xunit;

namespace Odmon.Worker.Tests
{
    public class HearingSelectorTests
    {
        [Fact]
        public void PickNearestUpcomingHearing_EmptyRows_ReturnsEmpty()
        {
            var now = new DateTime(2025, 6, 15, 10, 0, 0);
            var result = HearingSelector.PickNearestUpcomingHearing(Array.Empty<OdcanitDiaryEvent>(), now);
            Assert.Empty(result);
        }

        [Fact]
        public void PickNearestUpcomingHearing_NullRows_ReturnsEmpty()
        {
            var now = new DateTime(2025, 6, 15, 10, 0, 0);
            var result = HearingSelector.PickNearestUpcomingHearing(null!, now);
            Assert.Empty(result);
        }

        [Fact]
        public void PickNearestUpcomingHearing_ExcludesPastStartDate()
        {
            var now = new DateTime(2025, 6, 15, 10, 0, 0);
            var rows = new List<OdcanitDiaryEvent>
            {
                NewEvent(tikCounter: 1, start: now.AddDays(-1)),
                NewEvent(tikCounter: 2, start: now.AddHours(-1))
            };
            var result = HearingSelector.PickNearestUpcomingHearing(rows, now);
            Assert.Empty(result);
        }

        [Fact]
        public void PickNearestUpcomingHearing_IncludesFutureOnly_PicksNearestPerTikCounter()
        {
            var now = new DateTime(2025, 6, 15, 10, 0, 0);
            var rows = new List<OdcanitDiaryEvent>
            {
                NewEvent(tikCounter: 1, start: now.AddDays(3)),
                NewEvent(tikCounter: 1, start: now.AddDays(1)),
                NewEvent(tikCounter: 1, start: now.AddHours(2)),
                NewEvent(tikCounter: 2, start: now.AddDays(2))
            };
            var result = HearingSelector.PickNearestUpcomingHearing(rows, now);
            Assert.Equal(2, result.Count);
            Assert.Equal(now.AddHours(2), result[1].StartDate);
            Assert.Equal(now.AddDays(2), result[2].StartDate);
        }

        [Fact]
        public void PickNearestUpcomingHearing_TransferredFollowedByActiveReplacement_PicksActiveReplacement()
        {
            var now = new DateTime(2026, 7, 20, 10, 0, 0);
            var transferred = NewEvent(tikCounter: 26093, start: new DateTime(2026, 7, 26, 9, 30, 0), meetStatus: 2);
            var active = NewEvent(tikCounter: 26093, start: new DateTime(2026, 9, 6, 11, 0, 0), meetStatus: 0);

            var result = HearingSelector.PickNearestUpcomingHearing(new[] { transferred, active }, now);

            Assert.Single(result);
            Assert.Equal(active.StartDate, result[26093].StartDate);
            Assert.Equal(0, result[26093].MeetStatus);
        }

        [Fact]
        public void PickNearestUpcomingHearing_CancelledFollowedByActiveReplacement_PicksActiveReplacement()
        {
            var now = new DateTime(2026, 7, 20, 10, 0, 0);
            var cancelled = NewEvent(tikCounter: 26093, start: new DateTime(2026, 7, 26, 9, 30, 0), meetStatus: 1);
            var active = NewEvent(tikCounter: 26093, start: new DateTime(2026, 9, 6, 11, 0, 0), meetStatus: 0);

            var result = HearingSelector.PickNearestUpcomingHearing(new[] { cancelled, active }, now);

            Assert.Single(result);
            Assert.Equal(active.StartDate, result[26093].StartDate);
            Assert.Equal(0, result[26093].MeetStatus);
        }

        [Fact]
        public void PickNearestUpcomingHearing_CancelledOnlyFutureHearing_SelectsCancelledFallback()
        {
            var now = new DateTime(2026, 7, 20, 10, 0, 0);
            var cancelled = NewEvent(tikCounter: 26093, start: new DateTime(2026, 7, 26, 9, 30, 0), meetStatus: 1);

            var result = HearingSelector.PickNearestUpcomingHearing(new[] { cancelled }, now);

            Assert.Single(result);
            Assert.Equal(cancelled.StartDate, result[26093].StartDate);
            Assert.Equal(1, result[26093].MeetStatus);
        }

        [Fact]
        public void PickNearestUpcomingHearing_TransferredOnlyFutureHearing_SelectsTransferredFallback()
        {
            var now = new DateTime(2026, 7, 20, 10, 0, 0);
            var transferred = NewEvent(tikCounter: 26093, start: new DateTime(2026, 7, 26, 9, 30, 0), meetStatus: 2);

            var result = HearingSelector.PickNearestUpcomingHearing(new[] { transferred }, now);

            Assert.Single(result);
            Assert.Equal(transferred.StartDate, result[26093].StartDate);
            Assert.Equal(2, result[26093].MeetStatus);
        }

        [Fact]
        public void PickNearestUpcomingHearing_ActivePreferredOverCancelledAndTransferredRows()
        {
            var now = new DateTime(2026, 7, 20, 10, 0, 0);
            var transferred = NewEvent(tikCounter: 26093, start: new DateTime(2026, 7, 26, 9, 30, 0), meetStatus: 2);
            var cancelled = NewEvent(tikCounter: 26093, start: new DateTime(2026, 8, 1, 9, 30, 0), meetStatus: 1);
            var active = NewEvent(tikCounter: 26093, start: new DateTime(2026, 9, 6, 11, 0, 0), meetStatus: 0);

            var result = HearingSelector.PickNearestUpcomingHearing(new[] { transferred, cancelled, active }, now);

            Assert.Single(result);
            Assert.Equal(active.StartDate, result[26093].StartDate);
            Assert.Equal(0, result[26093].MeetStatus);
        }

        [Fact]
        public void PickNearestUpcomingHearing_MultipleCancelledFallbackRows_PicksNearestFutureCancelled()
        {
            var now = new DateTime(2026, 7, 20, 10, 0, 0);
            var laterCancelled = NewEvent(tikCounter: 26093, start: new DateTime(2026, 9, 6, 11, 0, 0), meetStatus: 1);
            var nearestCancelled = NewEvent(tikCounter: 26093, start: new DateTime(2026, 8, 1, 8, 30, 0), meetStatus: 1);

            var result = HearingSelector.PickNearestUpcomingHearing(new[] { laterCancelled, nearestCancelled }, now);

            Assert.Single(result);
            Assert.Equal(nearestCancelled.StartDate, result[26093].StartDate);
            Assert.Equal(1, result[26093].MeetStatus);
        }

        [Fact]
        public void PickNearestUpcomingHearing_CancelledFallbackPreferredOverEarlierTransferredFallback()
        {
            var now = new DateTime(2026, 7, 20, 10, 0, 0);
            var transferred = NewEvent(tikCounter: 26093, start: new DateTime(2026, 7, 26, 9, 30, 0), meetStatus: 2);
            var cancelled = NewEvent(tikCounter: 26093, start: new DateTime(2026, 9, 6, 11, 0, 0), meetStatus: 1);

            var result = HearingSelector.PickNearestUpcomingHearing(new[] { transferred, cancelled }, now);

            Assert.Single(result);
            Assert.Equal(cancelled.StartDate, result[26093].StartDate);
            Assert.Equal(1, result[26093].MeetStatus);
        }

        [Fact]
        public void PickNearestUpcomingHearing_MultipleFutureActiveHearings_PicksNearestActive()
        {
            var now = new DateTime(2026, 7, 20, 10, 0, 0);
            var laterActive = NewEvent(tikCounter: 26093, start: new DateTime(2026, 9, 6, 11, 0, 0), meetStatus: 0);
            var nearestActive = NewEvent(tikCounter: 26093, start: new DateTime(2026, 8, 1, 8, 30, 0), meetStatus: 0);

            var result = HearingSelector.PickNearestUpcomingHearing(new[] { laterActive, nearestActive }, now);

            Assert.Single(result);
            Assert.Equal(nearestActive.StartDate, result[26093].StartDate);
        }

        [Fact]
        public void PickNearestUpcomingHearing_ExcludesNullStartDate()
        {
            var now = new DateTime(2025, 6, 15, 10, 0, 0);
            var rows = new List<OdcanitDiaryEvent>
            {
                NewEvent(tikCounter: 1, start: null)
            };
            var result = HearingSelector.PickNearestUpcomingHearing(rows, now);
            Assert.Empty(result);
        }

        [Fact]
        public void PickNearestUpcomingHearing_ExcludesNullTikCounter()
        {
            var now = new DateTime(2025, 6, 15, 10, 0, 0);
            var rows = new List<OdcanitDiaryEvent>
            {
                NewEvent(tikCounter: null, start: now.AddDays(1))
            };
            var result = HearingSelector.PickNearestUpcomingHearing(rows, now);
            Assert.Empty(result);
        }

        private static OdcanitDiaryEvent NewEvent(int? tikCounter, DateTime? start, int meetStatus = 0)
        {
            return new OdcanitDiaryEvent
            {
                TikCounter = tikCounter,
                StartDate = start,
                JudgeName = "Judge",
                City = "City",
                MeetStatus = meetStatus
            };
        }
    }
}
