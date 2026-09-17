using System;
using System.Collections.Generic;
using Odmon.Worker.Models;

namespace Odmon.Worker.Services
{
    public sealed record HearingReconciliationPlan(
        int MeetStatus,
        string? StatusLabel,
        bool StatusChanged,
        bool StatusUpdateRequired,
        bool StartDateChanged,
        bool JudgeChanged,
        bool CityChanged,
        bool DateUpdateRequired,
        bool DateUpdateBlocked,
        string? JudgeName,
        string? City,
        IReadOnlyList<string> PlannedSteps)
    {
        public bool JudgeOrCityUpdateRequired => JudgeChanged || CityChanged;
        public bool HasRequiredMutations => PlannedSteps.Count > 0;
    }

    /// <summary>
    /// Testable helper for hearing sync update ordering.
    /// </summary>
    public static class HearingNearestSyncServiceHelper
    {
        public static HearingReconciliationPlan CreatePlan(
            OdcanitDiaryEvent hearing,
            HearingNearestSnapshot? snapshot,
            DateTime startDateUtc,
            string? effectiveCourtCity)
        {
            var meetStatus = hearing.MeetStatus ?? 0;
            var statusLabel = meetStatus switch
            {
                1 => "\u05de\u05d1\u05d5\u05d8\u05dc",
                2 => "\u05d4\u05d5\u05e2\u05d1\u05e8",
                _ => null
            };

            var hasJudgeName = !string.IsNullOrWhiteSpace(hearing.JudgeName);
            var hasCourtCity = !string.IsNullOrWhiteSpace(effectiveCourtCity);
            var judgeName = hasJudgeName ? hearing.JudgeName!.Trim() : null;
            var city = hasCourtCity ? effectiveCourtCity!.Trim() : null;

            var snapshotStartUtc = snapshot?.NearestStartDateUtc;
            var snapshotStatus = snapshot?.NearestMeetStatus;
            var startDateChanged = snapshotStartUtc == null
                || Math.Abs((startDateUtc - snapshotStartUtc.Value).TotalMinutes) > 1;
            var statusChanged = snapshotStatus == null || snapshotStatus.Value != meetStatus;
            var judgeChanged = hasJudgeName
                && (snapshot?.JudgeName == null
                    || !string.Equals(snapshot.JudgeName, judgeName, StringComparison.Ordinal));
            var cityChanged = hasCourtCity
                && (snapshot?.City == null
                    || !string.Equals(snapshot.City, city, StringComparison.Ordinal));

            // Active status is intentionally snapshot-only: it never overwrites Monday.
            var statusUpdateRequired = statusChanged && statusLabel != null;
            var dateUpdateRequired = startDateChanged && hasJudgeName && hasCourtCity;
            var dateUpdateBlocked = startDateChanged && !dateUpdateRequired;

            var plannedSteps = new List<string>();
            if (statusUpdateRequired)
            {
                plannedSteps.Add(meetStatus == 1 ? "SetStatus_Canceled" : "SetStatus_Transferred");
            }
            if (judgeChanged || cityChanged)
            {
                plannedSteps.Add("UpdateJudgeCity");
            }
            if (dateUpdateRequired)
            {
                plannedSteps.Add("UpdateHearingDate");
            }

            return new HearingReconciliationPlan(
                meetStatus,
                statusLabel,
                statusChanged,
                statusUpdateRequired,
                startDateChanged,
                judgeChanged,
                cityChanged,
                dateUpdateRequired,
                dateUpdateBlocked,
                judgeName,
                city,
                plannedSteps);
        }

        /// <summary>
        /// Computes planned step names in execution order for a given hearing state and snapshot.
        /// </summary>
        public static (IReadOnlyList<string> PlannedSteps, bool StartDateChanged, bool StatusChanged, bool JudgeOrCityChanged) ComputePlannedSteps(
            OdcanitDiaryEvent hearing,
            HearingNearestSnapshot? snapshot)
        {
            var startDateUtc = hearing.StartDate!.Value.Kind == DateTimeKind.Utc
                ? hearing.StartDate.Value
                : DateTime.SpecifyKind(hearing.StartDate.Value, DateTimeKind.Utc);
            var effectiveCourtCity = !string.IsNullOrWhiteSpace(hearing.City)
                ? hearing.City
                : hearing.CourtName;
            var plan = CreatePlan(hearing, snapshot, startDateUtc, effectiveCourtCity);
            var statusChanged = snapshot?.NearestMeetStatus != plan.MeetStatus;
            return (
                plan.PlannedSteps,
                plan.StartDateChanged,
                statusChanged,
                plan.JudgeOrCityUpdateRequired);
        }
    }
}
