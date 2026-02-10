using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;

namespace Odmon.Worker.Services
{
    public partial class SyncService
    {
        /// <summary>
        /// Determines which TikCounters to load based on OdcanitLoad configuration.
        /// Enforces allowlist if enabled, otherwise uses incremental change-feed + existing mappings.
        /// </summary>
        private async Task<int[]> DetermineTikCountersToLoadAsync(CancellationToken ct)
        {
            // ── Mode 1: Allowlist (debug / controlled rollout) ──
            if (_odcanitLoadOptions.EnableAllowList)
            {
                _logger.LogInformation("OdcanitLoad allowlist ENABLED");

                var allowedTikCounters = new HashSet<int>(_odcanitLoadOptions.TikCounters ?? new List<int>());

                var tikNumbers = _odcanitLoadOptions.TikNumbers ?? new List<string>();
                if (tikNumbers.Any())
                {
                    _logger.LogInformation("Resolving {Count} TikNumber(s) to TikCounters", tikNumbers.Count);
                    var resolved = await _odcanitReader.ResolveTikNumbersToCountersAsync(tikNumbers, ct);
                    foreach (var kvp in resolved)
                    {
                        allowedTikCounters.Add(kvp.Value);
                    }
                }

                if (allowedTikCounters.Count == 0)
                {
                    _logger.LogError(
                        "OdcanitLoad:EnableAllowList=true but allowlist is EMPTY after resolution. " +
                        "Configured TikCounters={ConfiguredTikCounters}, TikNumbers={ConfiguredTikNumbers}. " +
                        "FAIL FAST: At least one case must be specified in the allowlist.",
                        _odcanitLoadOptions.TikCounters?.Count ?? 0,
                        _odcanitLoadOptions.TikNumbers?.Count ?? 0);
                    return Array.Empty<int>();
                }

                var finalAllowList = allowedTikCounters.OrderBy(tc => tc).ToArray();
                _logger.LogInformation(
                    "OdcanitLoad allowlist resolved to {Count} TikCounter(s): [{TikCounters}]",
                    finalAllowList.Length,
                    string.Join(", ", finalAllowList));

                return finalAllowList;
            }

            // ── Mode 2: Production incremental (change-feed only, no full refresh) ──
            _logger.LogInformation("OdcanitLoad allowlist DISABLED - using incremental change-feed (listener mode)");

            // Determine watermark: last successful run timestamp from SyncLogs
            var sinceUtc = await GetChangeFeedWatermarkAsync(ct);

            _logger.LogInformation(
                "Change feed watermark: sinceUtc={SinceUtc:yyyy-MM-dd HH:mm:ss}",
                sinceUtc);

            IReadOnlyList<int> changedTikCounters;
            try
            {
                changedTikCounters = await _changeFeed.GetChangedTikCountersSinceAsync(sinceUtc, ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Change feed query failed. No TikCounters to process this run.");
                return Array.Empty<int>();
            }

            if (changedTikCounters.Count == 0)
            {
                _logger.LogInformation(
                    "Change feed returned 0 TikCounters since {SinceUtc:yyyy-MM-dd HH:mm:ss}. Nothing to process.",
                    sinceUtc);
                return Array.Empty<int>();
            }

            var result = changedTikCounters.Distinct().OrderBy(tc => tc).ToArray();
            _logger.LogInformation(
                "Change feed returned {RawCount} TikCounter(s) since {SinceUtc:yyyy-MM-dd HH:mm:ss}, {DistinctCount} distinct to process: [{TikCounters}]",
                changedTikCounters.Count,
                sinceUtc,
                result.Length,
                result.Length <= 50 ? string.Join(", ", result) : $"{string.Join(", ", result.Take(50))}... (truncated)");

            return result;
        }

        /// <summary>
        /// Determines the watermark (sinceUtc) for the change feed query.
        /// Uses the most recent successful SyncLog entry as the watermark.
        /// Falls back to UtcNow - 5 minutes on first run (no history).
        /// Subtracts a 2-minute safety overlap to avoid missing edge-case events.
        /// </summary>
        private async Task<DateTime> GetChangeFeedWatermarkAsync(CancellationToken ct)
        {
            const int safetyOverlapMinutes = 2;
            const int firstRunLookbackMinutes = 5;

            try
            {
                // Use the most recent SyncLog from SyncService as the watermark
                var lastRunUtc = await _integrationDb.SyncLogs
                    .AsNoTracking()
                    .Where(l => l.Source == "SyncService")
                    .MaxAsync(l => (DateTime?)l.CreatedAtUtc, ct);

                if (lastRunUtc.HasValue)
                {
                    var watermark = lastRunUtc.Value.AddMinutes(-safetyOverlapMinutes);
                    _logger.LogDebug(
                        "Watermark from SyncLogs: lastRunUtc={LastRunUtc:yyyy-MM-dd HH:mm:ss}, with {Overlap}min overlap -> {Watermark:yyyy-MM-dd HH:mm:ss}",
                        lastRunUtc.Value, safetyOverlapMinutes, watermark);
                    return watermark;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to read watermark from SyncLogs; using first-run default.");
            }

            // First run or empty SyncLogs: look back 5 minutes only
            var fallback = DateTime.UtcNow.AddMinutes(-firstRunLookbackMinutes);
            _logger.LogInformation(
                "No previous SyncLog found (first run). Using default watermark: {Watermark:yyyy-MM-dd HH:mm:ss} (now - {Minutes}min)",
                fallback, firstRunLookbackMinutes);
            return fallback;
        }
    }
}
