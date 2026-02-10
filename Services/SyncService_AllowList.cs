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

            // ── Mode 2: Production incremental (change-feed + existing mappings) ──
            _logger.LogInformation("OdcanitLoad allowlist DISABLED - using incremental change-feed + existing mappings");

            var allTikCounters = new HashSet<int>();

            // 2a) Existing mapped TikCounters (refresh all known cases)
            var boardId = _mondaySettings.CasesBoardId;
            var mappedTikCounters = await _integrationDb.MondayItemMappings
                .AsNoTracking()
                .Where(m => m.BoardId == boardId)
                .Select(m => m.TikCounter)
                .Distinct()
                .ToListAsync(ct);

            foreach (var tc in mappedTikCounters)
            {
                allTikCounters.Add(tc);
            }

            _logger.LogInformation(
                "Incremental load: {MappedCount} existing mapped TikCounter(s) for BoardId={BoardId}",
                mappedTikCounters.Count, boardId);

            // 2b) Recently changed TikCounters from Odcanit change feed
            try
            {
                var lastSyncUtc = await _integrationDb.MondayItemMappings
                    .AsNoTracking()
                    .Where(m => m.BoardId == boardId && m.LastSyncFromOdcanitUtc.HasValue)
                    .MaxAsync(m => (DateTime?)m.LastSyncFromOdcanitUtc, ct)
                    ?? DateTime.UtcNow.AddHours(-6);

                // Look back a bit further to catch edge cases
                var lookbackUtc = lastSyncUtc.AddMinutes(-30);

                var changedTikCounters = await _changeFeed.GetChangedTikCountersSinceAsync(lookbackUtc, ct);
                var newFromFeed = 0;
                foreach (var tc in changedTikCounters)
                {
                    if (allTikCounters.Add(tc))
                        newFromFeed++;
                }

                _logger.LogInformation(
                    "Incremental load: Change feed returned {FeedCount} TikCounter(s) since {SinceUtc:yyyy-MM-dd HH:mm}, {NewCount} new (not already mapped)",
                    changedTikCounters.Count, lookbackUtc, newFromFeed);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Change feed query failed; proceeding with {Count} existing mapped TikCounters only",
                    allTikCounters.Count);
            }

            if (allTikCounters.Count == 0)
            {
                _logger.LogWarning(
                    "No TikCounters found from mappings or change feed for BoardId={BoardId}. " +
                    "This may be normal for a first run; new cases will appear once created via the main sync.",
                    boardId);
                return Array.Empty<int>();
            }

            var result = allTikCounters.OrderBy(tc => tc).ToArray();
            _logger.LogInformation(
                "Incremental load: Total {Count} TikCounter(s) to process",
                result.Length);

            return result;
        }
    }
}
