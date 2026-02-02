using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Odmon.Worker.Models;
using Odmon.Worker.OdcanitAccess;

namespace Odmon.Worker.Services
{
    /// <summary>
    /// Default case source that reads from Odcanit via IOdcanitReader.
    /// </summary>
    public class OdcanitCaseSource : ICaseSource
    {
        private readonly IOdcanitReader _reader;

        public OdcanitCaseSource(IOdcanitReader reader)
        {
            _reader = reader;
        }

        public Task<List<OdcanitCase>> GetCasesByTikCountersAsync(IEnumerable<int> tikCounters, CancellationToken ct)
        {
            return _reader.GetCasesByTikCountersAsync(tikCounters, ct);
        }
    }
}

