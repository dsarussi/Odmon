using Odmon.Worker.Models;

namespace Odmon.Worker.OdcanitAccess
{
    public interface INetCourtDocumentReader
    {
        Task<long> GetMaxDecisionCounterAsync(CancellationToken ct);

        Task<List<NetCourtDocument>> GetDecisionDocumentsAfterCounterAsync(
            long lastSeenCounter,
            int maxBatchSize,
            CancellationToken ct);
    }
}
