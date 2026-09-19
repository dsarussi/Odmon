using System;
using System.Collections.Generic;
using System.Linq;
using Odmon.Worker.Models;

namespace Odmon.Worker.Services
{
    /// <summary>
    /// Selects the nearest upcoming hearing per TikCounter from source diary rows.
    /// </summary>
    public static class HearingSelector
    {
        /// <summary>
        /// Returns one row per TikCounter using this precedence:
        /// 1) nearest future active hearing;
        /// 2) nearest future cancelled hearing;
        /// 3) nearest future transferred hearing;
        /// 4) when no future row exists, the most recently elapsed cancelled or
        ///    transferred hearing inside the bounded recovery window.
        /// Past active hearings are never recovery candidates.
        /// </summary>
        /// <param name="rows">All diary event rows (e.g. from GetDiaryEventsByTikCountersAsync).</param>
        /// <param name="nowLocal">Current time in Israel local time (used as lower bound for StartDate).</param>
        /// <returns>Dictionary of TikCounter to the selected future or bounded-recovery diary row.</returns>
        public static IReadOnlyDictionary<int, OdcanitDiaryEvent> PickNearestUpcomingHearing(
            IEnumerable<OdcanitDiaryEvent> rows,
            DateTime nowLocal,
            TimeSpan? recoveryLookback = null)
        {
            if (rows == null)
            {
                return new Dictionary<int, OdcanitDiaryEvent>();
            }

            var lookback = recoveryLookback ?? TimeSpan.FromDays(30);
            if (lookback < TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(nameof(recoveryLookback), "Recovery lookback cannot be negative.");
            }

            var list = rows
                .Where(d => d.TikCounter.HasValue
                            && d.StartDate.HasValue
                            && GetSelectionPriority(d.MeetStatus ?? 0).HasValue)
                .ToList();

            var byTik = list
                .GroupBy(d => d.TikCounter!.Value)
                .ToDictionary(
                    g => g.Key,
                    g => SelectForCase(g, nowLocal, lookback))
                .Where(pair => pair.Value != null)
                .ToDictionary(pair => pair.Key, pair => pair.Value!);

            return byTik;
        }

        private static OdcanitDiaryEvent? SelectForCase(
            IEnumerable<OdcanitDiaryEvent> rows,
            DateTime nowLocal,
            TimeSpan recoveryLookback)
        {
            var future = rows
                .Where(d => d.StartDate!.Value >= nowLocal)
                .OrderBy(d => GetSelectionPriority(d.MeetStatus ?? 0)!.Value)
                .ThenBy(d => d.StartDate!.Value)
                .FirstOrDefault();

            if (future != null)
            {
                return future;
            }

            var recoveryCutoff = nowLocal - recoveryLookback;
            return rows
                .Where(d => d.StartDate!.Value < nowLocal
                            && d.StartDate.Value >= recoveryCutoff
                            && (d.MeetStatus == 1 || d.MeetStatus == 2))
                .OrderByDescending(d => d.StartDate!.Value)
                .ThenBy(d => GetSelectionPriority(d.MeetStatus ?? 0)!.Value)
                .FirstOrDefault();
        }

        private static int? GetSelectionPriority(int meetStatus)
        {
            return meetStatus switch
            {
                0 => 0,
                1 => 1,
                2 => 2,
                _ => null
            };
        }
    }
}
