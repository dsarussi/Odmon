using System;
using System.Collections.Generic;
using Odmon.Worker.Models;
using Odmon.Worker.Services;
using Xunit;

namespace Odmon.Worker.Tests
{
    /// <summary>
    /// Tests for hearing sync update ordering (reschedule vs new vs cancelled).
    /// </summary>
    public class HearingUpdateOrderingTests
    {
        [Fact]
        public void Reschedule_MeetStatus2_PlannedSteps_StatusFirst_ThenJudgeCity_ThenDate()
        {
            var snapshot = new HearingNearestSnapshot
            {
                NearestStartDateUtc = DateTime.UtcNow.AddDays(1),
                NearestMeetStatus = 0,
                JudgeName = "OldJudge",
                City = "OldCity"
            };
            var hearing = NewHearing(meetStatus: 2, start: DateTime.UtcNow.AddDays(2), judge: "NewJudge", city: "NewCity");
            var (planned, _, _, _) = HearingNearestSyncServiceHelper.ComputePlannedSteps(hearing, snapshot);
            Assert.Equal(3, planned.Count);
            Assert.Equal("SetStatus_הועבר", planned[0]);
            Assert.Equal("UpdateJudgeCity", planned[1]);
            Assert.Equal("UpdateHearingDate", planned[2]);
        }

        [Fact]
        public void NewActive_MeetStatus0_PlannedSteps_JudgeCity_ThenDate_ThenStatus()
        {
            HearingNearestSnapshot? snapshot = null;
            var hearing = NewHearing(meetStatus: 0, start: DateTime.UtcNow.AddDays(1), judge: "Judge", city: "City");
            var (planned, _, _, _) = HearingNearestSyncServiceHelper.ComputePlannedSteps(hearing, snapshot);
            Assert.Equal(3, planned.Count);
            Assert.Equal("UpdateJudgeCity", planned[0]);
            Assert.Equal("UpdateHearingDate", planned[1]);
            Assert.Equal("SetStatus_פעיל", planned[2]);
        }

        [Fact]
        public void Cancelled_MeetStatus1_OnlyStatus_NoDateChange()
        {
            var start = DateTime.UtcNow.AddDays(1);
            var snapshot = new HearingNearestSnapshot
            {
                NearestMeetStatus = 0,
                NearestStartDateUtc = start
            };
            var hearing = NewHearing(meetStatus: 1, start: start, judge: "J", city: "C");
            var (planned, _, _, _) = HearingNearestSyncServiceHelper.ComputePlannedSteps(hearing, snapshot);
            Assert.Single(planned);
            Assert.Equal("SetStatus_מבוטל", planned[0]);
        }

        [Fact]
        public void NoChange_SameAsSnapshot_PlannedStepsEmpty()
        {
            var start = DateTime.UtcNow.AddDays(1);
            var snapshot = new HearingNearestSnapshot
            {
                NearestStartDateUtc = start,
                NearestMeetStatus = 0,
                JudgeName = "Judge",
                City = "City"
            };
            var hearing = NewHearing(meetStatus: 0, start: start, judge: "Judge", city: "City");
            var (planned, _, _, _) = HearingNearestSyncServiceHelper.ComputePlannedSteps(hearing, snapshot);
            Assert.Empty(planned);
        }

        private static OdcanitDiaryEvent NewHearing(int meetStatus, DateTime? start, string judge, string city)
        {
            return new OdcanitDiaryEvent
            {
                TikCounter = 1,
                StartDate = start,
                JudgeName = judge,
                City = city,
                MeetStatus = meetStatus
            };
        }
    }
}
