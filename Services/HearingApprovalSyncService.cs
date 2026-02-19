using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
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
    /// Phase-2: Sync hearing approval status from Monday back to Odcanit (append Nispah records).
    /// </summary>
    public class HearingApprovalSyncService
    {
        private readonly IntegrationDbContext _integrationDb;
        private readonly IMondayClient _mondayClient;
        private readonly IOdcanitWriter _odcanitWriter;
        private readonly IConfiguration _config;
        private readonly MondaySettings _mondaySettings;
        private readonly ILogger<HearingApprovalSyncService> _logger;

        public HearingApprovalSyncService(
            IntegrationDbContext integrationDb,
            IMondayClient mondayClient,
            IOdcanitWriter odcanitWriter,
            IConfiguration config,
            IOptions<MondaySettings> mondayOptions,
            ILogger<HearingApprovalSyncService> logger)
        {
            _integrationDb = integrationDb;
            _mondayClient = mondayClient;
            _odcanitWriter = odcanitWriter;
            _config = config;
            _mondaySettings = mondayOptions.Value;
            _logger = logger;
        }

        public async Task SyncAsync(IEnumerable<OdcanitCase> cases, CancellationToken ct)
        {
            var enableWrites = _config.GetValue<bool>("OdcanitWrites:Enable", false);
            var dryRun = _config.GetValue<bool>("OdcanitWrites:DryRun", true);
            var mode = !enableWrites ? "disabled" : (dryRun ? "dryrun" : "live");

            _logger.LogInformation(
                "HearingApproval Phase2 mode resolved: Enable={Enable}, DryRun={DryRun}, Mode={Mode}",
                enableWrites,
                dryRun,
                mode);

            var casesBoardId = _mondaySettings.CasesBoardId != 0
                ? _mondaySettings.CasesBoardId
                : _mondaySettings.BoardId;

            foreach (var c in cases)
            {
                if (ct.IsCancellationRequested)
                {
                    break;
                }

                if (c.TikCounter <= 0)
                {
                    continue;
                }

                // Only items with an existing mapping on the cases board
                var mapping = await _integrationDb.MondayItemMappings
                    .AsNoTracking()
                    .FirstOrDefaultAsync(
                        m => m.TikCounter == c.TikCounter && m.BoardId == casesBoardId,
                        ct);

                if (mapping == null)
                {
                    continue;
                }

                var itemId = mapping.MondayItemId;

                var newStatus = await _mondayClient.GetHearingApprovalStatusAsync(itemId, ct);
                if (string.IsNullOrWhiteSpace(newStatus))
                {
                    continue;
                }

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

                var oldStatus = state.LastKnownStatus;

                if (oldStatus == newStatus)
                {
                    // Idempotent: nothing to do
                    continue;
                }

                _logger.LogInformation(
                    "HearingApproval changed: TikCounter={TikCounter}, ItemId={ItemId}, Old={Old}, New={New}, FirstDecision={FirstDecision}",
                    c.TikCounter,
                    itemId,
                    oldStatus ?? "<null>",
                    newStatus,
                    state.FirstDecision ?? "<null>");

                var nowUtc = DateTime.UtcNow;

                // Handle default "5" behavior
                if (newStatus == "5")
                {
                    if (!string.IsNullOrWhiteSpace(state.FirstDecision))
                    {
                        _logger.LogWarning(
                            "Invalid HearingApproval transition suppressed: TikCounter={TikCounter}, ItemId={ItemId}, FirstDecision={FirstDecision}, Old={Old}, New={New}",
                            c.TikCounter,
                            itemId,
                            state.FirstDecision,
                            oldStatus ?? "<null>",
                            newStatus);
                        // Do NOT update LastKnownStatus so it can be retried after manual fix
                        continue;
                    }

                    state.LastKnownStatus = newStatus;
                    state.UpdatedAtUtc = nowUtc;
                    await _integrationDb.SaveChangesAsync(ct);
                    continue;
                }

                // Only 1 or 2 are actionable
                if (newStatus != "1" && newStatus != "2")
                {
                    // Track but do not write
                    state.LastKnownStatus = newStatus;
                    state.UpdatedAtUtc = nowUtc;
                    await _integrationDb.SaveChangesAsync(ct);
                    continue;
                }

                // Require upcoming hearing
                if (c.HearingDate == null)
                {
                    _logger.LogWarning(
                        "Hearing approval present but no upcoming hearing; suppressed write for TikCounter={TikCounter}, ItemId={ItemId}",
                        c.TikCounter,
                        itemId);

                    state.LastKnownStatus = newStatus;
                    state.UpdatedAtUtc = nowUtc;
                    await _integrationDb.SaveChangesAsync(ct);
                    continue;
                }

                // First decision
                if (string.IsNullOrWhiteSpace(state.FirstDecision))
                {
                    state.FirstDecision = newStatus;
                }
                else
                {
                    // Enforce 1<->2 toggles; allow if old is null/5
                    if (!string.IsNullOrWhiteSpace(oldStatus) && oldStatus != "5")
                    {
                        var toggleOk =
                            (oldStatus == "1" && newStatus == "2") ||
                            (oldStatus == "2" && newStatus == "1");

                        if (!toggleOk)
                        {
                            _logger.LogWarning(
                                "Invalid HearingApproval transition suppressed: TikCounter={TikCounter}, ItemId={ItemId}, FirstDecision={FirstDecision}, Old={Old}, New={New}",
                                c.TikCounter,
                                itemId,
                                state.FirstDecision,
                                oldStatus ?? "<null>",
                                newStatus);
                            // Do NOT update LastKnownStatus so it can be retried
                            continue;
                        }
                    }
                }

                // Build Info text
                var policyHolderName = !string.IsNullOrWhiteSpace(c.PolicyHolderName)
                    ? c.PolicyHolderName
                    : "בעל פוליסה";

                var statusText = newStatus == "1" ? "מאשר הגעה" : "לא מאשר הגעה";
                var info = $"{policyHolderName} {statusText}";

                if (c.HearingDate.HasValue)
                {
                    var dtLocal = c.HearingDate.Value.Date + (c.HearingTime ?? TimeSpan.Zero);
                    info += $" | דיון: {dtLocal:dd/MM/yyyy HH\\:mm}";
                }

                if (!enableWrites || dryRun)
                {
                    _logger.LogInformation(
                        "HearingApproval Nispah write: mode={Mode}, TikCounter={TikCounter}, ItemId={ItemId}, Status={Status}, Info='{Info}'",
                        mode,
                        c.TikCounter,
                        itemId,
                        newStatus,
                        info);
                }
                else
                {
                    try
                    {
                        await _odcanitWriter.AppendNispahAsync(c, nowUtc, "אישור הגעה לדיון", info, ct);
                        state.LastWriteAtUtc = nowUtc;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(
                            ex,
                            "HearingApproval Nispah write failed: mode={Mode}, TikCounter={TikCounter}, ItemId={ItemId}, Status={Status}",
                            mode,
                            c.TikCounter,
                            itemId,
                            newStatus);
                        // Do NOT update LastKnownStatus so it can be retried
                        continue;
                    }
                }

                state.LastKnownStatus = newStatus;
                state.UpdatedAtUtc = nowUtc;
                await _integrationDb.SaveChangesAsync(ct);
            }
        }
    }
}

