using Odmon.Worker.Models;

namespace Odmon.Worker.OdcanitAccess
{
    public interface INetCourtDocumentReader
    {
        Task<List<NetCourtDocument>> GetDecisionDocumentsAsync(
            DateTime? createdSinceUtc,
            CancellationToken ct);
    }
}
