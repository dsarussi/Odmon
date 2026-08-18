using Odmon.Worker.Models;

namespace Odmon.Worker.OdcanitAccess
{
    public interface ICaseIntakeDocumentReader
    {
        Task<IReadOnlyList<OdcanitCaseDocument>> GetRelevantDocumentsAsync(
            int tikCounter,
            CancellationToken ct);
    }
}
