using Odmon.Worker.Models;

namespace Odmon.Worker.OdcanitAccess
{
    public sealed record TikNumberResolution(int? TikCounter, bool IsAmbiguous)
    {
        public bool IsResolved => !IsAmbiguous && TikCounter is > 0;
    }

    public interface IOdcanitReader
    {
        Task<List<OdcanitCase>> GetCasesCreatedOnDateAsync(DateTime date, CancellationToken ct);
        Task<List<OdcanitCase>> GetCasesByTikCountersAsync(IEnumerable<int> tikCounters, CancellationToken ct);
        /// <summary>Fetches all vwExportToOuterSystems_YomanData rows for the given TikCounters (no date/status filter).</summary>
        Task<List<OdcanitDiaryEvent>> GetDiaryEventsByTikCountersAsync(IEnumerable<int> tikCounters, CancellationToken ct);
        /// <summary>Resolves TikNumber strings (e.g., "9/900003") to TikCounter integers. Returns dictionary of resolved mappings.</summary>
        Task<Dictionary<string, int>> ResolveTikNumbersToCountersAsync(IEnumerable<string> tikNumbers, CancellationToken ct);
        /// <summary>
        /// Resolves exact TikNumbers while retaining ambiguity when one number maps
        /// to multiple distinct TikCounters. Implementations without a multi-row
        /// source retain their existing unique-resolution behavior.
        /// </summary>
        async Task<Dictionary<string, TikNumberResolution>> ResolveTikNumbersWithAmbiguityAsync(
            IEnumerable<string> tikNumbers,
            CancellationToken ct)
        {
            var resolved = await ResolveTikNumbersToCountersAsync(tikNumbers, ct);
            return resolved.ToDictionary(
                pair => pair.Key,
                pair => new TikNumberResolution(pair.Value, IsAmbiguous: false),
                StringComparer.Ordinal);
        }
        /// <summary>
        /// Returns all TikCounters from vwExportToOuterSystems_Files where tsCreateDate >= cutoffDate.
        /// Used by the bootstrap phase to discover eligible cases independently of the change feed.
        /// </summary>
        Task<List<int>> GetTikCountersSinceCutoffAsync(DateTime cutoffDate, CancellationToken ct);
    }
}

