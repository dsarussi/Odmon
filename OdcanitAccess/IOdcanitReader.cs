using Odmon.Worker.Models;

namespace Odmon.Worker.OdcanitAccess
{
    public interface IOdcanitReader
    {
        Task<List<OdcanitCase>> GetCasesCreatedOnDateAsync(DateTime date, CancellationToken ct);
        Task<List<OdcanitCase>> GetCasesByTikCountersAsync(IEnumerable<int> tikCounters, CancellationToken ct);
    }
}

