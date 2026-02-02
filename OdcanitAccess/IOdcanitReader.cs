using Odmon.Worker.Models;

namespace Odmon.Worker.OdcanitAccess
{
    public interface IOdcanitReader
    {
        Task<List<OdcanitCase>> GetCasesCreatedOnDateAsync(DateTime date, CancellationToken ct);
        Task<List<OdcanitCase>> GetCasesByTikCountersAsync(IEnumerable<int> tikCounters, CancellationToken ct);
        /// <summary>Fetches all vwExportToOuterSystems_YomanData rows for the given TikCounters (no date/status filter).</summary>
        Task<List<OdcanitDiaryEvent>> GetDiaryEventsByTikCountersAsync(IEnumerable<int> tikCounters, CancellationToken ct);
    }
}

