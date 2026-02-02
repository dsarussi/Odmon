using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Odmon.Worker.Models;

namespace Odmon.Worker.Services
{
    /// <summary>
    /// Abstraction over case sources (Odcanit vs Integration test tables).
    /// </summary>
    public interface ICaseSource
    {
        Task<List<OdcanitCase>> GetCasesByTikCountersAsync(IEnumerable<int> tikCounters, CancellationToken ct);
    }
}

