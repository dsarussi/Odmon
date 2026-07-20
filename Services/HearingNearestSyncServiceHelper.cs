using System;
using System.Collections.Generic;
using Odmon.Worker.Models;

namespace Odmon.Worker.Services
{
    /// <summary>
    /// Testable helper for hearing sync update ordering.
    /// </summary>
    public static class HearingNearestSyncServiceHelper
    {
        /// <summary>
        /// Computes planned step names in execution order for a given hearing state and snapshot.
        /// </summary>
        public static (IReadOnlyList<string> PlannedSteps, bool StartDateChanged, bool StatusChanged, bool JudgeOrCityChanged) ComputePlannedSteps(
            OdcanitDiaryEvent hearing,
            HearingNearestSnapshot? snapshot)
        {
            var meetStatus = hearing.MeetStatus ?? 0;
            var startDateUtc = hearing.StartDate!.Value.Kind == DateTimeKind.Utc
                ? hearing.StartDate.Value
                : DateTime.SpecifyKind(hearing.StartDate.Value, DateTimeKind.Utc);
            var judgeName = (hearing.JudgeName ?? "").Trim();
            var city = (hearing.City ?? "").Trim();

            var snapshotStartUtc = snapshot?.NearestStartDateUtc;
            var snapshotStatus = snapshot?.NearestMeetStatus;
            var snapshotJudge = snapshot?.JudgeName?.Trim();
            var snapshotCity = snapshot?.City?.Trim();

            var startDateChanged = startDateUtc != snapshotStartUtc;
            var statusChanged = meetStatus != snapshotStatus;
            var judgeOrCityChanged = judgeName != snapshotJudge || city != snapshotCity;

            var plannedSteps = new List<string>();

            if (meetStatus == 2)
            {
                if (statusChanged) plannedSteps.Add("SetStatus_Transferred");
                if (judgeOrCityChanged) plannedSteps.Add("UpdateJudgeCity");
                if (startDateChanged) plannedSteps.Add("UpdateHearingDate");
            }
            else if (meetStatus == 0)
            {
                if (statusChanged) plannedSteps.Add("SetStatus_Active");
                if (judgeOrCityChanged) plannedSteps.Add("UpdateJudgeCity");
                if (startDateChanged) plannedSteps.Add("UpdateHearingDate");
            }
            else if (meetStatus == 1)
            {
                if (statusChanged) plannedSteps.Add("SetStatus_Canceled");
            }

            return (plannedSteps, startDateChanged, statusChanged, judgeOrCityChanged);
        }
    }
}
