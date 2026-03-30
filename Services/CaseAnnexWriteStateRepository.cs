using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Odmon.Worker.Data;
using Odmon.Worker.Models;

namespace Odmon.Worker.Services
{
    public class CaseAnnexWriteStateRepository : ICaseAnnexWriteStateRepository
    {
        private readonly IntegrationDbContext _db;
        private readonly ILogger<CaseAnnexWriteStateRepository> _logger;

        private const int SlowQueryThresholdMs = 5000;

        public CaseAnnexWriteStateRepository(IntegrationDbContext db, ILogger<CaseAnnexWriteStateRepository> logger)
        {
            _db = db;
            _logger = logger;
        }

        public async Task<CaseAnnexWriteState> GetOrCreateStateAsync(int tikCounter, CancellationToken ct = default)
        {
            var sw = Stopwatch.StartNew();
            var state = await _db.CaseAnnexWriteStates
                .AsNoTracking()
                .FirstOrDefaultAsync(s => s.TikCounter == tikCounter, ct);
            sw.Stop();

            if (sw.ElapsedMilliseconds >= SlowQueryThresholdMs)
                _logger.LogWarning("SLOW_QUERY | CaseAnnexWriteState lookup: {ElapsedMs}ms, TikCounter={TikCounter}",
                    sw.ElapsedMilliseconds, tikCounter);

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
