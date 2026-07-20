using System;
using System.Collections.Generic;
using System.Linq;
using Odmon.Worker.Models;

namespace Odmon.Worker.Services
{
    /// <summary>
    /// Selects the nearest upcoming hearing per TikCounter from vwExportToOuterSystems_YomanData rows.
    /// </summary>
    public static class HearingSelector
    {
        /// <summary>
        /// Returns one row per TikCounter: nearest future active hearing, then nearest
        /// future cancelled fallback, then nearest future transferred fallback.
        /// </summary>
        /// <param name="rows">All diary event rows (e.g. from GetDiaryEventsByTikCountersAsync).</param>
        /// <param name="nowLocal">Current time in Israel local time (used as lower bound for StartDate).</param>
        /// <returns>Dictionary of TikCounter -> OdcanitDiaryEvent (nearest upcoming); only TikCounters that have at least one future row.</returns>
        public static IReadOnlyDictionary<int, OdcanitDiaryEvent> PickNearestUpcomingHearing(
            IEnumerable<OdcanitDiaryEvent> rows,
            DateTime nowLocal)
        {
            if (rows == null)
            {
                return new Dictionary<int, OdcanitDiaryEvent>();
            }

            var list = rows
                .Where(d => d.TikCounter.HasValue
                            && d.StartDate.HasValue
                            && d.StartDate.Value >= nowLocal
                            && GetSelectionPriority(d.MeetStatus ?? 0).HasValue)
                .ToList();

            var byTik = list
                .GroupBy(d => d.TikCounter!.Value)
                .ToDictionary(
                    g => g.Key,
                    g => g.OrderBy(d => GetSelectionPriority(d.MeetStatus ?? 0)!.Value)
                        .ThenBy(d => d.StartDate!.Value)
                        .First());

            return byTik;
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
