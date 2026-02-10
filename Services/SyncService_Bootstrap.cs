using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Odmon.Worker.Exceptions;
using Odmon.Worker.Models;

namespace Odmon.Worker.Services
{
    public partial class SyncService
    {
        /// <summary>
        /// Phase A — Bootstrap Onboarding.
        /// Discovers all cases with tsCreateDate >= CutoffDate from Odcanit,
        /// computes the set difference against already-mapped TikCounters in IntegrationDb,
        /// and creates Monday items for any newly-eligible, unmapped cases.
        ///
        /// This method is the ONLY place where Monday items are created.
        /// It does not depend on the change feed.
        /// It is idempotent: safe to run multiple times.
        /// </summary>
        internal async Task<BootstrapResult> RunBootstrapOnboardingAsync(
            string runId,
            long boardId,
            string groupId,
            bool testMode,
            bool dryRun,
            DateTime cutoffDate,
            int maxItems,
            CancellationToken ct)
        {
            var result = new BootstrapResult();
            var sw = Stopwatch.StartNew();

            // 1) Query Odcanit: all TikCounters with tsCreateDate >= cutoffDate
            var odcanitTikCounters = await _odcanitReader.GetTikCountersSinceCutoffAsync(cutoffDate, ct);
            result.TotalFromOdcanit = odcanitTikCounters.Count;

            // 2) Query IntegrationDb: all TikCounters already mapped for this board
            var mappedTikCounters = await _integrationDb.MondayItemMappings
                .AsNoTracking()
                .Where(m => m.BoardId == boardId)
                .Select(m => m.TikCounter)
                .Distinct()
                .ToListAsync(ct);
            var mappedSet = new HashSet<int>(mappedTikCounters);
            result.AlreadyMapped = mappedSet.Count;

            // 3) Compute: eligible = odcanitTikCounters EXCEPT mappedTikCounters
            var unmappedEligible = odcanitTikCounters
                .Where(tc => !mappedSet.Contains(tc))
                .Distinct()
                .ToList();

            if (unmappedEligible.Count == 0)
            {
                sw.Stop();
                _logger.LogInformation(
                    "BOOTSTRAP | No new cases to onboard. CutoffDate={CutoffDate:yyyy-MM-dd}, TotalFromOdcanit={TotalFromOdcanit}, AlreadyMapped={AlreadyMapped}, Duration={DurationMs}ms",
                    cutoffDate, result.TotalFromOdcanit, result.AlreadyMapped, sw.ElapsedMilliseconds);
                return result;
            }

            _logger.LogInformation(
                "BOOTSTRAP | Found {UnmappedCount} unmapped eligible case(s) to onboard. CutoffDate={CutoffDate:yyyy-MM-dd}, TotalFromOdcanit={TotalFromOdcanit}, AlreadyMapped={AlreadyMapped}",
                unmappedEligible.Count, cutoffDate, result.TotalFromOdcanit, result.AlreadyMapped);

            // 4) Load full case data from Odcanit for unmapped eligible TikCounters
            var casesToOnboard = await _odcanitReader.GetCasesByTikCountersAsync(unmappedEligible, ct);

            // Apply DocumentType derivation
            foreach (var c in casesToOnboard)
            {
                try
                {
                    c.DocumentType = DetermineDocumentTypeFromClientVisualId(c.ClientVisualID);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex,
                        "BOOTSTRAP | Failed to derive DocumentType for TikCounter={TikCounter}: {Error}",
                        c.TikCounter, ex.Message);
                }
            }

            // Respect maxItems
            var batch = (maxItems > 0 ? casesToOnboard.Take(maxItems) : casesToOnboard).ToList();

            // 5) Create Monday items for each eligible case
            foreach (var c in batch)
            {
                ct.ThrowIfCancellationRequested();

                // Defense-in-depth: verify tsCreateDate >= cutoffDate even here
                var caseDate = c.tsCreateDate?.Date;
                if (!caseDate.HasValue || caseDate.Value < cutoffDate)
                {
                    result.SkippedGuardrail++;
                    _logger.LogWarning(
                        "BOOTSTRAP GUARDRAIL | Blocked pre-cutoff creation. TikCounter={TikCounter}, tsCreateDate={CreateDate}, CutoffDate={CutoffDate:yyyy-MM-dd}",
                        c.TikCounter, caseDate?.ToString("yyyy-MM-dd") ?? "<null>", cutoffDate);
                    continue;
                }

                // Check again that no mapping was created between the initial check and now (race safety)
                var existingMapping = await _integrationDb.MondayItemMappings
                    .FirstOrDefaultAsync(m => m.TikCounter == c.TikCounter && m.BoardId == boardId, ct);
                if (existingMapping != null)
                {
                    result.AlreadyMapped++;
                    _logger.LogDebug(
                        "BOOTSTRAP | TikCounter={TikCounter} already mapped (race check). Skipping.",
                        c.TikCounter);
                    continue;
                }

                var (itemName, _) = BuildItemName(c, testMode);

                try
                {
                    if (!dryRun)
                    {
                        var (mondayItemId, retries) = await ExecuteWithRetryAsync(
                            () => CreateMondayItemAsync(c, boardId, groupId, itemName, testMode, ct),
                            "bootstrap_create", c.TikCounter, ct);

                        result.NewlyOnboarded++;
                        _logger.LogInformation(
                            "BOOTSTRAP | Created Monday item: TikCounter={TikCounter}, TikNumber={TikNumber}, MondayItemId={MondayItemId}, Retries={Retries}",
                            c.TikCounter, c.TikNumber, mondayItemId, retries);
                    }
                    else
                    {
                        result.NewlyOnboarded++;
                        _logger.LogInformation(
                            "BOOTSTRAP | DRY-RUN would create: TikCounter={TikCounter}, TikNumber={TikNumber}",
                            c.TikCounter, c.TikNumber);
                    }
                }
                catch (CriticalFieldValidationException critEx)
                {
                    result.Failed++;
                    _logger.LogError(
                        "BOOTSTRAP | CRITICAL VALIDATION FAILED: TikCounter={TikCounter}, Column={ColumnId}, Reason={Reason}",
                        c.TikCounter, critEx.ColumnId, critEx.ValidationReason);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    result.Failed++;
                    _logger.LogError(ex,
                        "BOOTSTRAP | Error creating Monday item: TikCounter={TikCounter}, Error={Error}",
                        c.TikCounter, ex.Message);
                    await PersistSyncFailureAsync(runId, c.TikCounter, c.TikNumber, boardId, "bootstrap_create", ex, MaxRetryAttempts, ct);
                }
            }

            sw.Stop();

            _logger.LogInformation(
                "BOOTSTRAP SUMMARY | CutoffDate={CutoffDate:yyyy-MM-dd} | TotalFromOdcanit={TotalFromOdcanit} | AlreadyMapped={AlreadyMapped} | " +
                "NewlyOnboarded={NewlyOnboarded} | SkippedGuardrail={SkippedGuardrail} | Failed={Failed} | Duration={DurationMs}ms",
                cutoffDate, result.TotalFromOdcanit, result.AlreadyMapped,
                result.NewlyOnboarded, result.SkippedGuardrail, result.Failed, sw.ElapsedMilliseconds);

            return result;
        }

        /// <summary>Result counters for bootstrap onboarding.</summary>
        internal class BootstrapResult
        {
            public int TotalFromOdcanit { get; set; }
            public int AlreadyMapped { get; set; }
            public int NewlyOnboarded { get; set; }
            public int SkippedGuardrail { get; set; }
            public int Failed { get; set; }
        }
    }
}
