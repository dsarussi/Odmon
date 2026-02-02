using System;
using System.Collections.Generic;
using Odmon.Worker.Models;

namespace Odmon.Worker.Services
{
    /// <summary>
    /// Testable helper for hearing sync update ordering (reschedule vs new vs cancelled).
    /// </summary>
    public static class HearingNearestSyncServiceHelper
    {
        /// <summary>
        /// Computes planned step names in execution order for a given hearing state and snapshot.
        /// </summary>
        /// <param name="hearing">Selected nearest upcoming hearing (required fields already validated).</param>
        /// <param name="snapshot">Last synced snapshot, or null if first time.</param>
        /// <returns>Planned step names in order; and change flags.</returns>
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
                if (statusChanged) plannedSteps.Add("SetStatus_הועבר");
                if (judgeOrCityChanged) plannedSteps.Add("UpdateJudgeCity");
                if (startDateChanged) plannedSteps.Add("UpdateHearingDate");
            }
            else if (meetStatus == 0)
            {
                if (judgeOrCityChanged) plannedSteps.Add("UpdateJudgeCity");
                if (startDateChanged) plannedSteps.Add("UpdateHearingDate");
                if (statusChanged) plannedSteps.Add("SetStatus_פעיל");
            }
            else if (meetStatus == 1)
            {
                if (statusChanged) plannedSteps.Add("SetStatus_מבוטל");
            }

            return (plannedSteps, startDateChanged, statusChanged, judgeOrCityChanged);
        }
    }
}
