using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Odmon.Worker.Models;

namespace Odmon.Worker.Data
{
    /// <summary>
    /// Centralized NOLOCK read-access layer for dbo.MondayItemMappings.
    ///
    /// Every query in this service executes against the table WITH (NOLOCK) to
    /// prevent lock-wait timeouts caused by concurrent sync writes (INSERT on
    /// bootstrap, UPDATE on reconcile).
    ///
    /// SAFETY: All write paths (duplicate prevention, entity mutation) continue
    /// to use normal EF Core isolation with the UNIQUE INDEX on TikCounter as
    /// the final safety net.  See docs/MAPPING_LOOKUP_NOLOCK_FIX.md.
    /// </summary>
    public sealed class MondayMappingReadService
    {
        private readonly IntegrationDbContext _db;
        private readonly ILogger<MondayMappingReadService> _logger;

        private const int DefaultBatchSize = 100;

        public MondayMappingReadService(
            IntegrationDbContext db,
            ILogger<MondayMappingReadService> logger)
        {
            _db = db;
            _logger = logger;
        }

        // ================================================================
        // Existence check
        // ================================================================

        /// <summary>Returns true if a mapping row exists for the given TikCounter + BoardId.</summary>
        public async Task<bool> ExistsAsync(int tikCounter, long boardId, CancellationToken ct)
        {
            const string sql = @"SELECT TOP(1) 1
FROM dbo.MondayItemMappings WITH (NOLOCK)
WHERE TikCounter = @tikCounter AND BoardId = @boardId";

            var rows = await _db.Database
                .SqlQueryRaw<int>(sql,
                    new SqlParameter("@tikCounter", tikCounter),
                    new SqlParameter("@boardId", boardId))
                .ToListAsync(ct);

            return rows.Count > 0;
        }

        // ================================================================
        // Bulk candidate lookup (with batching + progressive retry)
        // ================================================================

        public sealed record MappingLookupResult(
            HashSet<int> MappedTikCounters,
            HashSet<int> UnresolvedTikCounters);

        /// <summary>
        /// Returns the subset of <paramref name="candidates"/> that already
        /// have a mapping row for <paramref name="boardId"/>. Batches into
        /// chunks of <paramref name="batchSize"/> with progressive retry on
        /// failure (halves batch, retries failed items).
        ///
        /// IMPORTANT: on DB failure this method does NOT classify TikCounters as
        /// mapped or unmapped. It returns only confirmed mapped values and
        /// reports unresolved TikCounters separately for safe caller handling.
        /// </summary>
        public async Task<MappingLookupResult> GetMappedTikCountersForCandidatesAsync(
            long boardId,
            IReadOnlyList<int> candidates,
            CancellationToken ct,
            int batchSize = DefaultBatchSize)
        {
            if (candidates.Count == 0)
                return new MappingLookupResult(new HashSet<int>(), new HashSet<int>());

            var mappedResult = new HashSet<int>();
            var unresolved = new HashSet<int>();
            var sw = Stopwatch.StartNew();

            _logger.LogInformation(
                "MAPPING_LOOKUP | Starting candidate-scoped NOLOCK lookup: CandidateCount={CandidateCount}, BoardId={BoardId}, InitialBatchSize={BatchSize}",
                candidates.Count, boardId, batchSize);

            var pending = new List<int>(candidates);
            int totalAttempts = 0;

            while (pending.Count > 0 && batchSize >= 1)
            {
                var failedItems = new List<int>();

                for (int offset = 0; offset < pending.Count; offset += batchSize)
                {
                    ct.ThrowIfCancellationRequested();

                    var batch = pending.Skip(offset).Take(batchSize).ToList();
                    totalAttempts++;

                    try
                    {
                        var mapped = await QueryBatchNolockAsync(boardId, batch, ct);
                        foreach (var tc in mapped)
                            mappedResult.Add(tc);
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(
                            "MAPPING_LOOKUP | Batch failed: Attempt={Attempt}, BatchSize={BatchSize}, BoardId={BoardId}, Error={Error}, TikCounters=[{TikCounters}]",
                            totalAttempts, batch.Count, boardId, ex.Message, string.Join(",", batch));
                        failedItems.AddRange(batch);
                    }
                }

                if (failedItems.Count == 0)
                    break;

                if (batchSize <= 1)
                {
                    _logger.LogWarning(
                        "MAPPING_LOOKUP | Lookup unresolved after progressive retry. UnresolvedCount={UnresolvedCount}, BoardId={BoardId}. " +
                        "These TikCounters will be excluded from decision-making this run (no mapped/unmapped classification).",
                        failedItems.Count, boardId);

                    foreach (var tc in failedItems)
                        unresolved.Add(tc);
                    break;
                }

                batchSize = Math.Max(1, batchSize / 2);
                pending = failedItems;

                _logger.LogWarning(
                    "MAPPING_LOOKUP | Retrying with smaller batches: NewBatchSize={NewBatchSize}, RemainingCount={Remaining}, BoardId={BoardId}",
                    batchSize, pending.Count, boardId);
            }

            sw.Stop();
            _logger.LogInformation(
                "MAPPING_LOOKUP | Completed: MappedCount={MappedCount}, UnresolvedCount={UnresolvedCount}, CandidateCount={CandidateCount}, Attempts={Attempts}, FinalBatchSize={FinalBatchSize}, BoardId={BoardId}, Duration={DurationMs}ms",
                mappedResult.Count, unresolved.Count, candidates.Count, totalAttempts, batchSize, boardId, sw.ElapsedMilliseconds);

            return new MappingLookupResult(mappedResult, unresolved);
        }

        private async Task<List<int>> QueryBatchNolockAsync(
            long boardId, IReadOnlyList<int> tikCounterBatch, CancellationToken ct)
        {
            var paramNames = new string[tikCounterBatch.Count];
            var sqlParams = new SqlParameter[tikCounterBatch.Count + 1];
            sqlParams[0] = new SqlParameter("@boardId", boardId);

            for (int i = 0; i < tikCounterBatch.Count; i++)
            {
                paramNames[i] = $"@tc{i}";
                sqlParams[i + 1] = new SqlParameter(paramNames[i], tikCounterBatch[i]);
            }

            var sql = $@"SELECT DISTINCT m.TikCounter
FROM dbo.MondayItemMappings m WITH (NOLOCK)
WHERE m.BoardId = @boardId
  AND m.TikCounter IN ({string.Join(",", paramNames)})";

            return await _db.Database
                .SqlQueryRaw<int>(sql, sqlParams)
                .ToListAsync(ct);
        }

        // ================================================================
        // Single-entity lookups — tracked (for subsequent EF writes)
        // ================================================================

        /// <summary>Finds a mapping by TikCounter + BoardId. Entity is change-tracked.</summary>
        public Task<MondayItemMapping?> FindTrackedAsync(
            int tikCounter, long boardId, CancellationToken ct)
        {
            return _db.MondayItemMappings
                .FromSqlRaw(
                    "SELECT * FROM dbo.MondayItemMappings WITH (NOLOCK) WHERE TikCounter = {0} AND BoardId = {1}",
                    tikCounter, boardId)
                .FirstOrDefaultAsync(ct);
        }

        /// <summary>Finds a mapping by TikCounter only (legacy fallback). Entity is change-tracked.</summary>
        public Task<MondayItemMapping?> FindTrackedByTikCounterAsync(
            int tikCounter, CancellationToken ct)
        {
            return _db.MondayItemMappings
                .FromSqlRaw(
                    "SELECT * FROM dbo.MondayItemMappings WITH (NOLOCK) WHERE TikCounter = {0}",
                    tikCounter)
                .FirstOrDefaultAsync(ct);
        }

        // ================================================================
        // Single-entity lookups — read-only (AsNoTracking)
        // ================================================================

        /// <summary>Finds a mapping by TikCounter + BoardId. Not change-tracked.</summary>
        public Task<MondayItemMapping?> FindReadOnlyAsync(
            int tikCounter, long boardId, CancellationToken ct)
        {
            return _db.MondayItemMappings
                .FromSqlRaw(
                    "SELECT * FROM dbo.MondayItemMappings WITH (NOLOCK) WHERE TikCounter = {0} AND BoardId = {1}",
                    tikCounter, boardId)
                .AsNoTracking()
                .FirstOrDefaultAsync(ct);
        }

        // ================================================================
        // Board-wide load — read-only
        // ================================================================

        /// <summary>Loads all mappings for a board. Not change-tracked.</summary>
        public Task<List<MondayItemMapping>> GetAllByBoardReadOnlyAsync(
            long boardId, CancellationToken ct)
        {
            return _db.MondayItemMappings
                .FromSqlRaw(
                    "SELECT * FROM dbo.MondayItemMappings WITH (NOLOCK) WHERE BoardId = {0}",
                    boardId)
                .AsNoTracking()
                .ToListAsync(ct);
        }

        // ================================================================
        // Counts / aggregates
        // ================================================================

        /// <summary>Count of mappings created in the given UTC range.</summary>
        public async Task<int> CountCreatedInRangeAsync(
            DateTime startUtc, DateTime endUtc, CancellationToken ct)
        {
            return (await _db.Database.SqlQueryRaw<int>(
                @"SELECT COUNT(*) AS [Value] FROM dbo.MondayItemMappings WITH (NOLOCK)
WHERE CreatedAtUtc >= @start AND CreatedAtUtc <= @end",
                new SqlParameter("@start", startUtc),
                new SqlParameter("@end", endUtc))
                .ToListAsync(ct)).FirstOrDefault();
        }

        /// <summary>Count of mappings updated (LastSyncFromOdcanitUtc) in the given UTC range.</summary>
        public async Task<int> CountUpdatedInRangeAsync(
            DateTime startUtc, DateTime endUtc, CancellationToken ct)
        {
            return (await _db.Database.SqlQueryRaw<int>(
                @"SELECT COUNT(*) AS [Value] FROM dbo.MondayItemMappings WITH (NOLOCK)
WHERE LastSyncFromOdcanitUtc >= @start AND LastSyncFromOdcanitUtc <= @end",
                new SqlParameter("@start", startUtc),
                new SqlParameter("@end", endUtc))
                .ToListAsync(ct)).FirstOrDefault();
        }

        // ================================================================
        // Projections / queryable
        // ================================================================

        /// <summary>Mappings created in the given UTC range, ordered by CreatedAtUtc. Not tracked.</summary>
        public Task<List<MondayItemMapping>> GetCreatedInRangeReadOnlyAsync(
            DateTime startUtc, DateTime endUtc, CancellationToken ct)
        {
            return _db.MondayItemMappings
                .FromSqlRaw("SELECT * FROM dbo.MondayItemMappings WITH (NOLOCK)")
                .AsNoTracking()
                .Where(m => m.CreatedAtUtc >= startUtc && m.CreatedAtUtc <= endUtc)
                .OrderBy(m => m.CreatedAtUtc)
                .ToListAsync(ct);
        }

        /// <summary>
        /// Returns an <see cref="IQueryable{MondayItemMapping}"/> backed by
        /// NOLOCK raw SQL, suitable for LINQ composition (joins, projections).
        /// </summary>
        public IQueryable<MondayItemMapping> NolockQueryable()
        {
            return _db.MondayItemMappings
                .FromSqlRaw("SELECT * FROM dbo.MondayItemMappings WITH (NOLOCK)")
                .AsNoTracking();
        }

        // ================================================================
        // HearingBackfill-specific
        // ================================================================

        /// <summary>Returns mapped TikNumbers (subset of <paramref name="tikNumbers"/>) for the given board.</summary>
        public async Task<List<string>> GetMappedTikNumbersAsync(
            long boardId, IReadOnlyList<string?> tikNumbers, CancellationToken ct)
        {
            if (tikNumbers.Count == 0)
                return new List<string>();

            var paramNames = new string[tikNumbers.Count];
            var sqlParams = new SqlParameter[tikNumbers.Count + 1];
            sqlParams[0] = new SqlParameter("@boardId", boardId);

            for (int i = 0; i < tikNumbers.Count; i++)
            {
                paramNames[i] = $"@tn{i}";
                sqlParams[i + 1] = new SqlParameter(paramNames[i], (object?)tikNumbers[i] ?? DBNull.Value);
            }

            var sql = $@"SELECT DISTINCT m.TikNumber
FROM dbo.MondayItemMappings m WITH (NOLOCK)
WHERE m.BoardId = @boardId
  AND m.TikNumber IN ({string.Join(",", paramNames)})";

            return await _db.Database
                .SqlQueryRaw<string>(sql, sqlParams)
                .ToListAsync(ct);
        }

    }
}
