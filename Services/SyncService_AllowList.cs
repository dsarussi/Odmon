using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Odmon.Worker.Services
{
    public partial class SyncService
    {
        /// <summary>
        /// Determines which TikCounters to load based on OdcanitLoad configuration.
        /// Enforces allowlist if enabled, otherwise uses production behavior.
        /// </summary>
        private async Task<int[]> DetermineTikCountersToLoadAsync(CancellationToken ct)
        {
            // Check if allowlist is enabled
            if (_odcanitLoadOptions.EnableAllowList)
            {
                _logger.LogInformation("OdcanitLoad allowlist ENABLED");

                // Start with configured TikCounters
                var allowedTikCounters = new HashSet<int>(_odcanitLoadOptions.TikCounters ?? new List<int>());

                // Resolve TikNumbers to TikCounters
                var tikNumbers = _odcanitLoadOptions.TikNumbers ?? new List<string>();
                if (tikNumbers.Any())
                {
                    _logger.LogInformation(
                        "Resolving {Count} TikNumber(s) to TikCounters",
                        tikNumbers.Count);

                    var resolved = await _odcanitReader.ResolveTikNumbersToCountersAsync(tikNumbers, ct);
                    
                    foreach (var kvp in resolved)
                    {
                        allowedTikCounters.Add(kvp.Value);
                    }
                }

                // Validate allowlist is not empty
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
            else
            {
                _logger.LogInformation("OdcanitLoad allowlist DISABLED - using production Sync:TikCounters behavior");

                // Production behavior: use Sync:TikCounters
                var tikCounterSection = _config.GetSection("Sync:TikCounters");
                var tikCounters = tikCounterSection.Get<int[]>() ?? Array.Empty<int>();

                if (tikCounters.Length == 0)
                {
                    _logger.LogError(
                        "Sync:TikCounters configuration is missing or empty. " +
                        "Worker must have explicit TikCounter list to fetch data. " +
                        "No data will be processed.");
                    return Array.Empty<int>();
                }

                _logger.LogInformation(
                    "Using Sync:TikCounters configuration: {Count} TikCounter(s): [{TikCounters}]",
                    tikCounters.Length,
                    string.Join(", ", tikCounters));

                return tikCounters;
            }
        }
    }
}
