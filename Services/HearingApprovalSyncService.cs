using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Odmon.Worker.Data;
using Odmon.Worker.Models;
using Odmon.Worker.Monday;
using Odmon.Worker.OdcanitAccess;

namespace Odmon.Worker.Services
{
    /// <summary>
    /// Detects changes in the Monday hearing-approval status column (color_mkzbmv1b)
    /// and writes an annex to Odcanit for every real approval/rejection transition.
    /// State tracking via <see cref="MondayHearingApprovalState"/> prevents duplicates.
    /// </summary>
    public class HearingApprovalSyncService
    {
        private const string NispahTypeName = "אישור הגעה לדיון";

        private const string StatusIndexApproved = "1";   // מאשר הגעה
        private const string StatusIndexRejected = "2";   // לא מאשר הגעה
        private const string StatusIndexDefault  = "5";   // עוד לא אישר הגעה

        private const string AnnexTextApproved = "אישר הגעה לדיון";
        private const string AnnexTextRejected = "לא אישר הגעה לדיון";

        internal const string SourceKindHearingApproval = "HearingApproval";

        private readonly IntegrationDbContext _integrationDb;
        private readonly IMondayClient _mondayClient;
        private readonly IOdcanitWriter _odcanitWriter;
        private readonly IConfiguration _config;
        private readonly MondaySettings _mondaySettings;
        private readonly ILogger<HearingApprovalSyncService> _logger;
        private readonly MondayMappingReadService _mappingReader;

        public HearingApprovalSyncService(
            IntegrationDbContext integrationDb,
            IMondayClient mondayClient,
            IOdcanitWriter odcanitWriter,
            IConfiguration config,
            IOptions<MondaySettings> mondayOptions,
            ILogger<HearingApprovalSyncService> logger,
            MondayMappingReadService mappingReader)
        {
            _integrationDb = integrationDb;
            _mondayClient = mondayClient;
            _odcanitWriter = odcanitWriter;
            _config = config;
            _mondaySettings = mondayOptions.Value;
            _logger = logger;
            _mappingReader = mappingReader;
        }

        public async Task SyncAsync(IEnumerable<OdcanitCase> cases, CancellationToken ct)
        {
            var enableWrites = _config.GetValue<bool>("OdcanitWrites:Enable", false);
            var dryRun = _config.GetValue<bool>("OdcanitWrites:DryRun", true);
            var mode = !enableWrites ? "disabled" : (dryRun ? "dryrun" : "live");

            _logger.LogInformation(
                "HearingApproval sync started: Enable={Enable}, DryRun={DryRun}, Mode={Mode}",
                enableWrites, dryRun, mode);

            if (!enableWrites)
            {
                _logger.LogInformation("HearingApproval sync skipped — writes are disabled");
                return;
            }

            var casesBoardId = _mondaySettings.CasesBoardId != 0
                ? _mondaySettings.CasesBoardId
                : _mondaySettings.BoardId;

            foreach (var c in cases)
            {
                if (ct.IsCancellationRequested) break;
                if (c.TikCounter <= 0)
                {
                    _logger.LogWarning(
                        "HearingApproval skipped case with non-positive TikCounter={TikCounter}, TikNumber={TikNumber} — Odcanit write-back requires a real TikCounter",
                        c.TikCounter, c.TikNumber);
                    continue;
                }

                var mapping = await _mappingReader.FindReadOnlyAsync(c.TikCounter, casesBoardId, ct);
                if (mapping == null) continue;

                var itemId = mapping.MondayItemId;

                var currentIndex = await _mondayClient.GetHearingApprovalStatusAsync(itemId, ct);
                if (string.IsNullOrWhiteSpace(currentIndex)) continue;

                _logger.LogInformation(
                    "HearingApproval read: TikCounter={TikCounter}, ItemId={ItemId}, CurrentIndex={CurrentIndex}",
                    c.TikCounter, itemId, currentIndex);

                var state = await _integrationDb.MondayHearingApprovalStates
                    .FirstOrDefaultAsync(s => s.BoardId == casesBoardId && s.MondayItemId == itemId, ct);

                if (state == null)
                {
                    state = new MondayHearingApprovalState
                    {
                        BoardId = casesBoardId,
                        MondayItemId = itemId,
                        TikCounter = c.TikCounter,
                        UpdatedAtUtc = DateTime.UtcNow
                    };
                    _integrationDb.MondayHearingApprovalStates.Add(state);
                }

                var previousIndex = state.LastKnownStatus;

                _logger.LogInformation(
                    "HearingApproval state: TikCounter={TikCounter}, PreviousIndex={Previous}, CurrentIndex={Current}",
                    c.TikCounter, previousIndex ?? "<null>", currentIndex);

                if (previousIndex == currentIndex)
                {
                    _logger.LogDebug(
                        "HearingApproval unchanged: TikCounter={TikCounter}, Status={Status} — skipping",
                        c.TikCounter, currentIndex);
                    continue;
                }

                _logger.LogInformation(
                    "HearingApproval change detected: TikCounter={TikCounter}, From={From}, To={To}",
                    c.TikCounter, previousIndex ?? "<null>", currentIndex);

                var annexText = GetAnnexText(currentIndex);
                var nowUtc = DateTime.UtcNow;

                if (annexText == null)
                {
                    if (dryRun)
                    {
                        _logger.LogInformation(
                            "HearingApproval annex skipped [dryrun] (non-actionable status): TikCounter={TikCounter}, Status={Status} — state NOT advanced",
                            c.TikCounter, currentIndex);
                        continue;
                    }

                    _logger.LogInformation(
                        "HearingApproval annex skipped (non-actionable status): TikCounter={TikCounter}, Status={Status}",
                        c.TikCounter, currentIndex);

                    state.LastKnownStatus = currentIndex;
                    state.UpdatedAtUtc = nowUtc;
                    await _integrationDb.SaveChangesAsync(ct);
                    continue;
                }

                if (dryRun)
                {
                    _logger.LogInformation(
                        "HearingApproval annex [dryrun]: TikCounter={TikCounter}, Text='{AnnexText}' — state NOT advanced",
                        c.TikCounter, annexText);
                    continue;
                }

                NispahWriteLog writeLog;
                try
                {
                    await _odcanitWriter.AppendNispahAsync(c, nowUtc, NispahTypeName, annexText, ct);
                    state.LastWriteAtUtc = nowUtc;

                    writeLog = BuildWriteLog(c.TikCounter, c.TikNumber, itemId, annexText, nowUtc, failed: false);

                    _logger.LogInformation(
                        "HearingApproval annex written: TikCounter={TikCounter}, Text='{AnnexText}'",
                        c.TikCounter, annexText);
                }
                catch (Exception ex)
                {
                    writeLog = BuildWriteLog(c.TikCounter, c.TikNumber, itemId, annexText, nowUtc, failed: true, ex.Message);
                    _integrationDb.NispahWriteLogs.Add(writeLog);
                    await TrySaveWriteLogAsync(ct);

                    _logger.LogError(ex,
                        "HearingApproval annex write FAILED: TikCounter={TikCounter}, Text='{AnnexText}' — status NOT advanced",
                        c.TikCounter, annexText);
                    continue;
                }

                _integrationDb.NispahWriteLogs.Add(writeLog);
                state.LastKnownStatus = currentIndex;
                state.UpdatedAtUtc = nowUtc;
                await _integrationDb.SaveChangesAsync(ct);

                _logger.LogInformation(
                    "HearingApproval state updated: TikCounter={TikCounter}, NewStatus={NewStatus}",
                    c.TikCounter, currentIndex);
            }
        }

        /// <summary>
        /// Maps a Monday status index to the exact annex text to write.
        /// Returns null for statuses that should not produce an annex.
        /// </summary>
        private static string? GetAnnexText(string statusIndex) => statusIndex switch
        {
            StatusIndexApproved => AnnexTextApproved,
            StatusIndexRejected => AnnexTextRejected,
            _ => null
        };

        internal static NispahWriteLog BuildWriteLog(
            int tikCounter, string tikNumber, long sourceItemId,
            string annexText, DateTime nowUtc,
            bool failed, string? errorMessage = null)
        {
            return new NispahWriteLog
            {
                TikCounter = tikCounter,
                TikVisualId = tikNumber,
                NispahType = NispahTypeName,
                SourceKind = SourceKindHearingApproval,
                SourceItemId = sourceItemId,
                InfoHash = ComputeSha256(annexText),
                CreatedAtUtc = nowUtc,
                Failed = failed,
                ErrorMessage = errorMessage?.Length > 2000 ? errorMessage[..2000] : errorMessage
            };
        }

        internal static string ComputeSha256(string input)
        {
            var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(input));
            return Convert.ToHexString(bytes).ToLowerInvariant();
        }

        private async Task TrySaveWriteLogAsync(CancellationToken ct)
        {
            try
            {
                await _integrationDb.SaveChangesAsync(ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "HearingApproval failed to persist NispahWriteLog (non-fatal)");
            }
        }
    }
}

