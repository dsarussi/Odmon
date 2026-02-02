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

        private static OdcanitDiaryEvent NewEvent(int? tikCounter, DateTime? start)
        {
            return new OdcanitDiaryEvent
            {
                TikCounter = tikCounter,
                StartDate = start,
                JudgeName = "Judge",
                City = "City",
                MeetStatus = 0
            };
        }
    }
}
