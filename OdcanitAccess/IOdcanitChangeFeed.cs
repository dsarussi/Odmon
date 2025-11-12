using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Odmon.Worker.Models;

namespace Odmon.Worker.OdcanitAccess
{
    public interface IOdcanitChangeFeed
    {
        Task<IReadOnlyList<int>> GetChangedTikCountersSinceAsync(DateTime sinceUtc, CancellationToken ct);
        Task<List<OdcanitCase>> GetCasesByTikCountersAsync(IEnumerable<int> tikCounters, CancellationToken ct);
    }
}

