using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Odmon.Worker.Configuration;
using Odmon.Worker.Data;
using Odmon.Worker.Models;
using Odmon.Worker.Monday;
using Odmon.Worker.OdcanitAccess;

namespace Odmon.Worker.Services
{
    /// <summary>
    /// One-time backfill: scans existing Monday case items and writes hearing-approval
    /// annexes for cases that already have an approved/rejected status.
    /// Reuses the same annex text mapping as <see cref="HearingApprovalSyncService"/>.
    /// </summary>
    public class HearingApprovalBackfillService
    {
        private const string NispahTypeName = "אישור הגעה לדיון";
        private const string StatusIndexApproved = "1";
        private const string StatusIndexRejected = "2";
        private const string AnnexTextApproved = "אישר הגעה לדיון";
        private const string AnnexTextRejected = "לא אישר הגעה לדיון";

        private readonly IntegrationDbContext _integrationDb;
        private readonly IMondayClient _mondayClient;
        private readonly IOdcanitWriter _odcanitWriter;
        private readonly MondaySettings _mondaySettings;
        private readonly HearingApprovalBackfillSettings _settings;
        private readonly ILogger<HearingApprovalBackfillService> _logger;
        private readonly MondayMappingReadService _mappingReader;

        public HearingApprovalBackfillService(
            IntegrationDbContext integrationDb,
            IMondayClient mondayClient,
            IOdcanitWriter odcanitWriter,
            IOptions<MondaySettings> mondayOptions,
            IOptions<HearingApprovalBackfillSettings> settings,
            ILogger<HearingApprovalBackfillService> logger,
            MondayMappingReadService mappingReader)
        {
            _integrationDb = integrationDb;
            _mondayClient = mondayClient;
            _odcanitWriter = odcanitWriter;
            _mondaySettings = mondayOptions.Value;
            _settings = settings.Value;
            _logger = logger;
            _mappingReader = mappingReader;
        }

        public async Task<HearingApprovalBackfillResult> RunAsync(CancellationToken ct)
        {
            var result = new HearingApprovalBackfillResult();
            var sw = Stopwatch.StartNew();
            var dryRun = _settings.DryRun;

            var casesBoardId = _mondaySettings.CasesBoardId != 0
                ? _mondaySettings.CasesBoardId
                : _mondaySettings.BoardId;

            _logger.LogInformation(
                "HEARING_APPROVAL_BACKFILL | Starting | BoardId={BoardId}, DryRun={DryRun}, MaxItems={MaxItems}, OnlyTikCounters={OnlyTikCounters}, ThrottleMs={ThrottleMs}",
                casesBoardId, dryRun, _settings.MaxItems,
                _settings.OnlyTikCounters != null ? string.Join(",", _settings.OnlyTikCounters) : "<all>",
                _settings.ThrottleMs);

            var allMappings = await _mappingReader.GetAllByBoardReadOnlyAsync(casesBoardId, ct);

            _logger.LogInformation(
                "HEARING_APPROVAL_BACKFILL | Loaded {Count} mappings for board {BoardId}",
                allMappings.Count, casesBoardId);

            IEnumerable<MondayItemMapping> mappings = allMappings;

            if (_settings.OnlyTikCounters is { Length: > 0 })
            {
                var allowed = new HashSet<int>(_settings.OnlyTikCounters);
                mappings = mappings.Where(m => allowed.Contains(m.TikCounter));
            }

            mappings = mappings
                .Where(m => m.TikCounter > 0 && !string.IsNullOrWhiteSpace(m.TikNumber));

            if (_settings.MaxItems > 0)
                mappings = mappings.Take(_settings.MaxItems);

            var list = mappings.ToList();
            result.Scanned = list.Count;

            _logger.LogInformation(
                "HEARING_APPROVAL_BACKFILL | Processing {Count} mappings after filters",
                list.Count);

            foreach (var mapping in list)
            {
                if (ct.IsCancellationRequested) break;

                try
                {
                    await ProcessMappingAsync(mapping, casesBoardId, dryRun, result, ct);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    result.Failed++;
                    _logger.LogError(ex,
                        "HEARING_APPROVAL_BACKFILL | Unexpected error for TikCounter={TikCounter}, ItemId={ItemId}",
                        mapping.TikCounter, mapping.MondayItemId);
                }

                if (_settings.ThrottleMs > 0)
                    await Task.Delay(_settings.ThrottleMs, ct);
            }

            sw.Stop();

            _logger.LogInformation(
                "HEARING_APPROVAL_BACKFILL | Completed in {ElapsedMs}ms | Scanned={Scanned}, Actionable={Actionable}, Written={Written}, SkippedNoStatus={SkippedNoStatus}, SkippedAlreadyProcessed={SkippedAlreadyProcessed}, SkippedDryRun={SkippedDryRun}, Failed={Failed}",
                sw.ElapsedMilliseconds,
                result.Scanned, result.Actionable, result.Written,
                result.SkippedNoStatus, result.SkippedAlreadyProcessed, result.SkippedDryRun,
                result.Failed);

            return result;
        }

        private async Task ProcessMappingAsync(
            MondayItemMapping mapping,
            long boardId,
            bool dryRun,
            HearingApprovalBackfillResult result,
            CancellationToken ct)
        {
            var tikCounter = mapping.TikCounter;
            var itemId = mapping.MondayItemId;

            var currentIndex = await _mondayClient.GetHearingApprovalStatusAsync(itemId, ct);

            if (string.IsNullOrWhiteSpace(currentIndex))
            {
                result.SkippedNoStatus++;
                _logger.LogDebug(
                    "HEARING_APPROVAL_BACKFILL | No status: TikCounter={TikCounter}, ItemId={ItemId}",
                    tikCounter, itemId);
                return;
            }

            var annexText = GetAnnexText(currentIndex);
            if (annexText == null)
            {
                result.SkippedNoStatus++;
                _logger.LogDebug(
                    "HEARING_APPROVAL_BACKFILL | Non-actionable status: TikCounter={TikCounter}, ItemId={ItemId}, Status={Status}",
                    tikCounter, itemId, currentIndex);
                return;
            }

            result.Actionable++;

            var state = await _integrationDb.MondayHearingApprovalStates
                .FirstOrDefaultAsync(s => s.BoardId == boardId && s.MondayItemId == itemId, ct);

            if (state != null && state.LastKnownStatus == currentIndex)
            {
                result.SkippedAlreadyProcessed++;
                _logger.LogDebug(
                    "HEARING_APPROVAL_BACKFILL | Already processed: TikCounter={TikCounter}, ItemId={ItemId}, Status={Status}",
                    tikCounter, itemId, currentIndex);
                return;
            }

            if (dryRun)
            {
                result.SkippedDryRun++;
                _logger.LogInformation(
                    "HEARING_APPROVAL_BACKFILL | [dryrun] Would write: TikCounter={TikCounter}, ItemId={ItemId}, Text='{AnnexText}' — state NOT advanced",
                    tikCounter, itemId, annexText);
                return;
            }

            var stubCase = new OdcanitCase { TikCounter = tikCounter, TikNumber = mapping.TikNumber! };
            var nowUtc = DateTime.UtcNow;

            try
            {
                await _odcanitWriter.AppendNispahAsync(stubCase, nowUtc, NispahTypeName, annexText, ct);
            }
            catch (Exception ex)
            {
                result.Failed++;
                _logger.LogError(ex,
                    "HEARING_APPROVAL_BACKFILL | Annex write FAILED: TikCounter={TikCounter}, ItemId={ItemId}, Text='{AnnexText}' — state NOT advanced",
                    tikCounter, itemId, annexText);
                return;
            }

            if (state == null)
            {
                state = new MondayHearingApprovalState
                {
                    BoardId = boardId,
                    MondayItemId = itemId,
                    TikCounter = tikCounter,
                    LastKnownStatus = currentIndex,
                    LastWriteAtUtc = nowUtc,
                    UpdatedAtUtc = nowUtc
                };
                _integrationDb.MondayHearingApprovalStates.Add(state);
            }
            else
            {
                state.LastKnownStatus = currentIndex;
                state.LastWriteAtUtc = nowUtc;
                state.UpdatedAtUtc = nowUtc;
            }

            await _integrationDb.SaveChangesAsync(ct);
            result.Written++;

            _logger.LogInformation(
                "HEARING_APPROVAL_BACKFILL | Annex written + state updated: TikCounter={TikCounter}, ItemId={ItemId}, Text='{AnnexText}', Status={Status}",
                tikCounter, itemId, annexText, currentIndex);
        }

        private static string? GetAnnexText(string statusIndex) => statusIndex switch
        {
            StatusIndexApproved => AnnexTextApproved,
            StatusIndexRejected => AnnexTextRejected,
            _ => null
        };
    }

    public class HearingApprovalBackfillResult
    {
        public int Scanned { get; set; }
        public int Actionable { get; set; }
        public int Written { get; set; }
        public int SkippedNoStatus { get; set; }
        public int SkippedAlreadyProcessed { get; set; }
        public int SkippedDryRun { get; set; }
        public int Failed { get; set; }
    }
}
