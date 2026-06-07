using Odmon.Worker.Models;

namespace Odmon.Worker.OdcanitAccess
{
    public interface INetCourtCaseResolver
    {
        Task<List<OdcanitCase>> GetCasesByTikCountersAsync(
            IEnumerable<int> tikCounters,
            CancellationToken ct);
    }

    public class NetCourtCaseResolver : INetCourtCaseResolver
    {
        private readonly SqlOdcanitReader _reader;

        public NetCourtCaseResolver(SqlOdcanitReader reader)
        {
            _reader = reader;
        }

        public Task<List<OdcanitCase>> GetCasesByTikCountersAsync(
            IEnumerable<int> tikCounters,
            CancellationToken ct)
            => _reader.GetCasesByTikCountersAsync(tikCounters, ct);
    }
}
