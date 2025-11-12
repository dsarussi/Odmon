using Odmon.Worker.Models;

namespace Odmon.Worker.OdcanitAccess
{
    public interface IOdcanitReader
    {
        Task<List<OdcanitCase>> GetCasesUpdatedSinceAsync(DateTime lastSyncUtc, CancellationToken ct);
    }

    public interface IOdcanitWriter
    {
        // For future Monday → Odcanit updates; stub for now.
    }
}

