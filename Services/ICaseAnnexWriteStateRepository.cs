using Odmon.Worker.Models;

namespace Odmon.Worker.Services
{
    /// <summary>
    /// Persistent per-case annex write state (e.g. Accident Story written at most once per TikCounter).
    /// </summary>
    public interface ICaseAnnexWriteStateRepository
    {
        Task<CaseAnnexWriteState> GetOrCreateStateAsync(int tikCounter, CancellationToken ct = default);
        Task MarkAccidentStoryWrittenAsync(int tikCounter, string? runId = null, CancellationToken ct = default);
    }
}
