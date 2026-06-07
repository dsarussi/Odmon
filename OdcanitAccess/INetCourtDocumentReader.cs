using Odmon.Worker.Models;

namespace Odmon.Worker.OdcanitAccess
{
    public interface INetCourtDocumentReader
    {
        Task<List<NetCourtDocument>> GetDecisionDocumentsFromDocDateAsync(
            DateTime startFromDocDate,
            CancellationToken ct);
    }
}
