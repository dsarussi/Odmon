using Odmon.Worker.Models;

namespace Odmon.Worker.OdcanitAccess
{
    public interface IOdcanitReader
    {
        Task<List<OdcanitCase>> GetCasesCreatedOnDateAsync(DateTime date, CancellationToken ct);
        Task<List<OdcanitCase>> GetCasesByTikCountersAsync(IEnumerable<int> tikCounters, CancellationToken ct);
        /// <summary>Fetches all vwExportToOuterSystems_YomanData rows for the given TikCounters (no date/status filter).</summary>
        Task<List<OdcanitDiaryEvent>> GetDiaryEventsByTikCountersAsync(IEnumerable<int> tikCounters, CancellationToken ct);
        /// <summary>Resolves TikNumber strings (e.g., "9/1808") to TikCounter integers. Returns dictionary of resolved mappings.</summary>
        Task<Dictionary<string, int>> ResolveTikNumbersToCountersAsync(IEnumerable<string> tikNumbers, CancellationToken ct);
        /// <summary>
        /// Returns all TikCounters from vwExportToOuterSystems_Files where tsCreateDate >= cutoffDate.
        /// Used by the bootstrap phase to discover eligible cases independently of the change feed.
        /// </summary>
        Task<List<int>> GetTikCountersSinceCutoffAsync(DateTime cutoffDate, CancellationToken ct);

        /// <summary>
        /// Returns TikCounters from vwExportToOuterSystems_Files where tsModifyDate >= sinceUtc,
        /// restricted to the provided eligibleMappedTikCounters set.
        /// Used as a deterministic fallback when the change feed may miss edits.
        /// Chunks the IN clause to avoid SQL parameter limits.
        /// </summary>
        Task<List<int>> GetModifiedTikCountersSinceAsync(
            DateTime sinceUtc,
            IReadOnlyCollection<int> eligibleMappedTikCounters,
            CancellationToken ct);
    }
}

