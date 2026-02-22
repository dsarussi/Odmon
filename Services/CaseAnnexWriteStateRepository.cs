using Microsoft.EntityFrameworkCore;
using Odmon.Worker.Data;
using Odmon.Worker.Models;

namespace Odmon.Worker.Services
{
    public class CaseAnnexWriteStateRepository : ICaseAnnexWriteStateRepository
    {
        private readonly IntegrationDbContext _db;

        public CaseAnnexWriteStateRepository(IntegrationDbContext db)
        {
            _db = db;
        }

        public async Task<CaseAnnexWriteState> GetOrCreateStateAsync(int tikCounter, CancellationToken ct = default)
        {
            var state = await _db.CaseAnnexWriteStates.AsNoTracking().FirstOrDefaultAsync(s => s.TikCounter == tikCounter, ct);
            if (state != null)
                return state;

            state = new CaseAnnexWriteState { TikCounter = tikCounter, AccidentStoryAnnexWritten = false };
            _db.CaseAnnexWriteStates.Add(state);
            try
            {
                await _db.SaveChangesAsync(ct);
                return state;
            }
            catch (DbUpdateException)
            {
                _db.Entry(state).State = EntityState.Detached;
                state = await _db.CaseAnnexWriteStates.AsNoTracking().FirstOrDefaultAsync(s => s.TikCounter == tikCounter, ct);
                return state ?? new CaseAnnexWriteState { TikCounter = tikCounter, AccidentStoryAnnexWritten = false };
            }
        }

        public async Task MarkAccidentStoryWrittenAsync(int tikCounter, string? runId = null, CancellationToken ct = default)
        {
            var state = await _db.CaseAnnexWriteStates.FirstOrDefaultAsync(s => s.TikCounter == tikCounter, ct);
            if (state == null)
            {
                state = new CaseAnnexWriteState { TikCounter = tikCounter };
                _db.CaseAnnexWriteStates.Add(state);
            }
            state.AccidentStoryAnnexWritten = true;
            state.AccidentStoryAnnexWrittenAtUtc = DateTime.UtcNow;
            state.AccidentStoryAnnexWrittenRunId = runId;
            await _db.SaveChangesAsync(ct);
        }
    }
}
