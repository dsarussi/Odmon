using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Odmon.Worker.Data;
using Odmon.Worker.Models;
using Odmon.Worker.Services;
using Odmon.Worker.Voicenter;

namespace Odmon.Worker.Workers
{
    /// <summary>
    /// Background service that:
    /// 1) Processes the email send queue (non-blocking for the sync loop).
    /// 2) Sends a daily summary email at 08:00 Israel time.
    /// 3) Sends a digest email every 15 minutes if there are suppressed alerts.
    /// </summary>
    public sealed class EmailBackgroundService : BackgroundService
    {
        private readonly EmailNotifier _emailNotifier;
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly ILogger<EmailBackgroundService> _logger;
        private readonly IConfiguration _config;

        private DateTime _lastDigestUtc = DateTime.MinValue;
        private DateOnly _lastDailySummaryIsraelDate = DateOnly.MinValue;

        public EmailBackgroundService(
            EmailNotifier emailNotifier,
            IServiceScopeFactory scopeFactory,
            ILogger<EmailBackgroundService> logger,
            IConfiguration config)
        {
            _emailNotifier = emailNotifier;
            _scopeFactory = scopeFactory;
            _logger = logger;
            _config = config;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            LogSmtpConfigValidation();

            var emailEnabled = _config.GetValue<bool>("Email:Enabled", false);
            if (!emailEnabled)
            {
                _logger.LogInformation("Email monitoring is DISABLED (Email:Enabled=false). Queue/digest disabled; daily summary will still be computed and logged.");
            }
            else
            {
                _logger.LogInformation("EmailBackgroundService started. Queue processing + daily summary + digest active.");
            }

            // Process queue (only when enabled) and run periodic tasks concurrently.
            // Daily summary is always computed and logged; sent only when enabled.
            var queueTask = emailEnabled ? ProcessQueueAsync(stoppingToken) : Task.Delay(Timeout.Infinite, stoppingToken);
            var periodicTask = RunPeriodicTasksAsync(stoppingToken);

            await Task.WhenAll(queueTask, periodicTask);
        }

        private void LogSmtpConfigValidation()
        {
            var host = _config["Email:SmtpHost"];
            var port = _config.GetValue<int>("Email:SmtpPort", 0);
            var useTls = _config.GetValue<bool>("Email:UseTls", false);
            var username = _config["Email:Username"];
            var hasPassword = !string.IsNullOrWhiteSpace(_config["Email:Password"]);
            var recipients = _config.GetSection("Email:Recipients").Get<string[]>() ?? Array.Empty<string>();
            var enabled = _config.GetValue<bool>("Email:Enabled", false);

            _logger.LogInformation(
                "EMAIL CONFIG | Enabled={Enabled}, Host={Host}, Port={Port}, UseTls={UseTls}, Username={Username}, HasPassword={HasPassword}, RecipientCount={RecipientCount}",
                enabled, host ?? "<not set>", port, useTls,
                string.IsNullOrWhiteSpace(username) ? "<not set>" : username,
                hasPassword, recipients.Length);

            if (enabled)
            {
                if (string.IsNullOrWhiteSpace(host))
                    _logger.LogWarning("EMAIL CONFIG WARN | SmtpHost is not configured");
                if (string.IsNullOrWhiteSpace(username) || !hasPassword)
                    _logger.LogWarning("EMAIL CONFIG WARN | SMTP credentials incomplete — emails will fail with auth error");
                if (recipients.Length == 0)
                    _logger.LogWarning("EMAIL CONFIG WARN | No recipients configured — emails will be skipped");
            }
        }

        // ================================================================
        // Queue processor — sends emails from the channel
        // ================================================================

        private async Task ProcessQueueAsync(CancellationToken ct)
        {
            try
            {
                await foreach (var message in _emailNotifier.Reader.ReadAllAsync(ct))
                {
                    try
                    {
                        await _emailNotifier.SendEmailAsync(message, ct);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        _logger.LogWarning(ex, "Failed to process queued email: {Subject}", message.Subject);
                    }

                    // Small delay between emails to avoid Gmail throttling
                    await Task.Delay(1000, ct);
                }
            }
            catch (OperationCanceledException) { /* normal shutdown */ }
        }

        // ================================================================
        // Periodic tasks: daily summary + digest
        // ================================================================

        private async Task RunPeriodicTasksAsync(CancellationToken ct)
        {
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    // Check every 60 seconds
                    await Task.Delay(TimeSpan.FromSeconds(60), ct);

                    await TrySendDailySummaryAsync(ct);
                    await TrySendDigestAsync(ct);
                }
            }
            catch (OperationCanceledException) { /* normal shutdown */ }
        }

        // ================================================================
        // Daily summary — once per day at configured Israel time
        // ================================================================

        private async Task TrySendDailySummaryAsync(CancellationToken ct)
        {
            try
            {
                var israelTz = SyncService.GetIsraelTimeZone();
                var nowIsrael = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, israelTz);
                var todayIsrael = DateOnly.FromDateTime(nowIsrael);

                // Parse configured time (default 08:00)
                var timeStr = _config["Email:DailySummaryTimeIsrael"] ?? "08:00";
                if (!TimeOnly.TryParse(timeStr, out var targetTime))
                    targetTime = new TimeOnly(8, 0);

                var nowTime = TimeOnly.FromDateTime(nowIsrael);

                // Send if: past target time today AND haven't sent for today yet.
                // Do not gate on WorkerCoordinator: DocumentIngestionWorker can hold the
                // lease for long runs and would starve the daily summary indefinitely.
                if (todayIsrael > _lastDailySummaryIsraelDate && nowTime >= targetTime)
                {
                    _lastDailySummaryIsraelDate = todayIsrael;
                    await SendDailySummaryEmailAsync(todayIsrael, ct);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Daily summary check failed. Will retry next cycle.");
            }
        }

        private async Task SendDailySummaryEmailAsync(DateOnly israelDate, CancellationToken ct)
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<IntegrationDbContext>();
                var mappingReader = scope.ServiceProvider.GetRequiredService<MondayMappingReadService>();

                var israelTz = SyncService.GetIsraelTimeZone();
                var yesterdayIsrael = israelDate.AddDays(-1);
                var (startUtc, endUtc) = GetYesterdayIsraelUtcRange(israelTz, yesterdayIsrael);

                // Section 1 — Overview counts (yesterday Israel time)
                var casesCreatedCount = await mappingReader.CountCreatedInRangeAsync(startUtc, endUtc, ct);

                var hearingsSyncedCount = await db.HearingNearestSnapshots
                    .AsNoTracking()
                    .Where(h => h.LastSyncedAtUtc >= startUtc && h.LastSyncedAtUtc <= endUtc)
                    .CountAsync(ct);

                var itemsUpdatedCount = await mappingReader.CountUpdatedInRangeAsync(startUtc, endUtc, ct);

                var allFailuresInWindow = await db.SyncFailures
                    .AsNoTracking()
                    .Where(f => f.OccurredAtUtc >= startUtc && f.OccurredAtUtc <= endUtc)
                    .ToListAsync(ct);
                var realFailures = allFailuresInWindow
                    .Where(f => FailureClassifier.IsRealFailure(f.ErrorType, f.Operation))
                    .ToList();
                var groupedFailures = realFailures
                    .GroupBy(f => (CaseNumber: f.TikNumber ?? "", Operation: f.Operation ?? "", RootCause: f.ErrorType ?? "Unknown"))
                    .Select(g => (g.Key.CaseNumber, g.Key.Operation, g.Key.RootCause, Count: g.Count(), FirstOccurrence: g.Min(x => x.OccurredAtUtc)))
                    .OrderBy(x => x.FirstOccurrence)
                    .ToList();
                var realFailureCount = groupedFailures.Count;

                // Section 2 — Cases created yesterday (table, NOLOCK)
                var casesCreatedEntities = await mappingReader.GetCreatedInRangeReadOnlyAsync(startUtc, endUtc, ct);
                var casesCreated = casesCreatedEntities
                    .Select(m => new { m.TikNumber, m.CreatedAtUtc })
                    .ToList();

                // Section 3 — Hearings synced yesterday (proxy for hearing activity; NOLOCK on mappings)
                var hearingsSynced = await (
                    from h in db.HearingNearestSnapshots.AsNoTracking()
                    where h.LastSyncedAtUtc >= startUtc && h.LastSyncedAtUtc <= endUtc
                    join m in mappingReader.NolockQueryable() on h.TikCounter equals m.TikCounter
                    orderby h.LastSyncedAtUtc
                    select new { m.TikNumber, h.LastSyncedAtUtc }
                ).ToListAsync(ct);

                // Section 5 — System notes (from SyncRunMetrics in window)
                // Section — Document Ingestion Failures
                var docIngestionFailures = await db.MondayDocumentImports
                    .AsNoTracking()
                    .Where(d => d.Status == DocumentImportStatus.Failed && d.UpdatedAtUtc >= startUtc && d.UpdatedAtUtc <= endUtc)
                    .OrderBy(d => d.UpdatedAtUtc)
                    .ToListAsync(ct);

                // Section — Voicenter call summary stats
                var voicenterWritten = await db.NispahWriteLogs
                    .AsNoTracking()
                    .CountAsync(w => w.SourceKind == VoicenterCallSummaryService.SourceKind
                                     && !w.Failed
                                     && w.CreatedAtUtc >= startUtc && w.CreatedAtUtc <= endUtc, ct);
                var voicenterFailed = await db.NispahWriteLogs
                    .AsNoTracking()
                    .Where(w => w.SourceKind == VoicenterCallSummaryService.SourceKind
                                && w.Failed
                                && w.CreatedAtUtc >= startUtc && w.CreatedAtUtc <= endUtc)
                    .ToListAsync(ct);
                VoicenterRunResult? lastVcResult;
                using (var vcScope = _scopeFactory.CreateScope())
                {
                    var vcWorker = vcScope.ServiceProvider
                        .GetServices<IHostedService>()
                        .OfType<VoicenterCallSummaryWorker>()
                        .FirstOrDefault();
                    lastVcResult = vcWorker?.LastRunResult;
                }

                var runMetricsInWindow = await db.SyncRunMetrics
                    .AsNoTracking()
                    .Where(m => m.StartedAtUtc >= startUtc && m.StartedAtUtc <= endUtc)
                    .ToListAsync(ct);
                var circuitBreakerTripped = runMetricsInWindow.Any(m => m.CircuitBreakerTripped);
                var totalRunFailures = runMetricsInWindow.Sum(m => m.Failed + m.BootstrapFailed);
                var highFailureNote = realFailureCount > 10 || totalRunFailures > 15;

                var subject = $"ODMON Daily Summary — {yesterdayIsrael:yyyy-MM-dd}";
                var body = BuildDailySummaryHtml(
                    yesterdayIsrael,
                    casesCreatedCount,
                    hearingsSyncedCount,
                    itemsUpdatedCount,
                    realFailureCount,
                    casesCreated.Select(c => (c.TikNumber ?? "", c.CreatedAtUtc)).ToList(),
                    hearingsSynced.Select(x => (x.TikNumber ?? "", x.LastSyncedAtUtc)).ToList(),
                    groupedFailures,
                    docIngestionFailures,
                    voicenterWritten, voicenterFailed, lastVcResult,
                    circuitBreakerTripped,
                    highFailureNote);

                if (_config.GetValue<bool>("Email:Enabled", false))
                {
                    await _emailNotifier.SendDailySummaryAsync(subject, body, ct);
                }

                _logger.LogInformation(
                    "DAILY SUMMARY SENT | Date={Date}, CasesCreated={Cases}, HearingsSynced={Hearings}, ItemsUpdated={Updated}, RealFailures={Failures}",
                    yesterdayIsrael, casesCreatedCount, hearingsSyncedCount, itemsUpdatedCount, realFailureCount);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Failed to send daily summary email.");
            }
        }

        /// <summary>Returns (startUtc, endUtc) for the given Israel date 00:00–23:59:59.999 Israel time.</summary>
        private static (DateTime startUtc, DateTime endUtc) GetYesterdayIsraelUtcRange(TimeZoneInfo israelTz, DateOnly dateIsrael)
        {
            var startIsrael = dateIsrael.ToDateTime(TimeOnly.MinValue);
            var endIsrael = dateIsrael.ToDateTime(new TimeOnly(23, 59, 59, 999));
            var startUtc = TimeZoneInfo.ConvertTimeToUtc(startIsrael, israelTz);
            var endUtc = TimeZoneInfo.ConvertTimeToUtc(endIsrael, israelTz);
            return (startUtc, endUtc);
        }

        private static string BuildDailySummaryHtml(
            DateOnly date,
            int casesCreatedCount,
            int hearingsSyncedCount,
            int itemsUpdatedCount,
            int realFailureCount,
            List<(string TikNumber, DateTime CreatedAtUtc)> casesCreated,
            List<(string TikNumber, DateTime LastSyncedAtUtc)> hearingsSynced,
            List<(string CaseNumber, string Operation, string RootCause, int Count, DateTime FirstOccurrence)> groupedFailures,
            List<MondayDocumentImport> docIngestionFailures,
            int voicenterWritten, List<NispahWriteLog> voicenterFailed, Voicenter.VoicenterRunResult? lastVcResult,
            bool circuitBreakerTripped,
            bool highFailureNote)
        {
            static string E(string s) => System.Net.WebUtility.HtmlEncode(s ?? string.Empty);

            var sb = new StringBuilder();
            sb.AppendLine("<html><body style='font-family:Arial,sans-serif;'>");
            sb.AppendLine($"<h2>ODMON Daily Summary — {date:yyyy-MM-dd}</h2>");
            sb.AppendLine("<p>Previous day (00:00–23:59 Israel time).</p>");

            // Section 1 — Daily Overview
            sb.AppendLine("<h3 style='margin:16px 0 8px;'>Daily Overview</h3>");
            sb.AppendLine("<table style='border-collapse:collapse; width:400px;'>");
            sb.AppendLine($"<tr><td style='padding:4px 12px 4px 0;'>Cases created in Monday yesterday</td><td style='padding:4px;'>{casesCreatedCount}</td></tr>");
            sb.AppendLine($"<tr><td style='padding:4px 12px 4px 0;'>Hearings synced yesterday</td><td style='padding:4px;'>{hearingsSyncedCount}</td></tr>");
            sb.AppendLine($"<tr><td style='padding:4px 12px 4px 0;'>Items updated yesterday</td><td style='padding:4px;'>{itemsUpdatedCount}</td></tr>");
            var failStyle = realFailureCount > 0 ? "color:red;font-weight:bold;" : "";
            sb.AppendLine($"<tr><td style='padding:4px 12px 4px 0;'>Real failures yesterday</td><td style='padding:4px;{failStyle}'>{realFailureCount}</td></tr>");
            var docFailTotal = docIngestionFailures.Count;
            var docFailScene = docIngestionFailures.Count(d => string.Equals(d.ColumnId, "file_mkyet713", StringComparison.OrdinalIgnoreCase));
            var docFailStyle = docFailTotal > 0 ? "color:#d35400;font-weight:bold;" : "";
            sb.AppendLine($"<tr><td style='padding:4px 12px 4px 0;'>Document ingestion failures</td><td style='padding:4px;{docFailStyle}'>{docFailTotal} (scene-docs: {docFailScene})</td></tr>");
            sb.AppendLine("</table>");

            // Section 2 — Cases Created Yesterday
            sb.AppendLine("<hr/>");
            sb.AppendLine("<h3 style='margin:16px 0 8px;'>Cases Created Yesterday</h3>");
            if (casesCreated.Count == 0)
                sb.AppendLine("<p>None.</p>");
            else
            {
                sb.AppendLine("<table style='border-collapse:collapse;border:1px solid #ddd;' cellpadding='6'>");
                sb.AppendLine("<tr style='background:#f5f5f5;'><th style='border:1px solid #ddd;'>Case Number</th><th style='border:1px solid #ddd;'>Created Time (UTC)</th></tr>");
                foreach (var r in casesCreated)
                {
                    sb.AppendLine($"<tr><td style='border:1px solid #ddd;'>{E(r.TikNumber)}</td><td style='border:1px solid #ddd;'>{r.CreatedAtUtc:yyyy-MM-dd HH:mm} UTC</td></tr>");
                }
                sb.AppendLine("</table>");
            }

            // Section 3 — Hearings synced yesterday (sync proxy; not "created")
            sb.AppendLine("<hr/>");
            sb.AppendLine("<h3 style='margin:16px 0 8px;'>Hearings Synced Yesterday</h3>");
            sb.AppendLine("<p><small>Hearing data synced to Monday (LastSyncedAtUtc).</small></p>");
            if (hearingsSynced.Count == 0)
                sb.AppendLine("<p>None.</p>");
            else
            {
                sb.AppendLine("<table style='border-collapse:collapse;border:1px solid #ddd;' cellpadding='6'>");
                sb.AppendLine("<tr style='background:#f5f5f5;'><th style='border:1px solid #ddd;'>Case Number</th><th style='border:1px solid #ddd;'>Synced Time (UTC)</th></tr>");
                foreach (var r in hearingsSynced)
                {
                    sb.AppendLine($"<tr><td style='border:1px solid #ddd;'>{E(r.TikNumber)}</td><td style='border:1px solid #ddd;'>{r.LastSyncedAtUtc:yyyy-MM-dd HH:mm} UTC</td></tr>");
                }
                sb.AppendLine("</table>");
            }

            // Section 4 — Failures That Require Attention (real failures only, grouped by Case+Operation+RootCause)
            sb.AppendLine("<hr/>");
            sb.AppendLine("<h3 style='margin:16px 0 8px;'>Failures That Require Attention</h3>");
            if (groupedFailures.Count == 0)
                sb.AppendLine("<p>None.</p>");
            else
            {
                sb.AppendLine("<table style='border-collapse:collapse;border:1px solid #ddd;' cellpadding='6'>");
                sb.AppendLine("<tr style='background:#f5f5f5;'><th style='border:1px solid #ddd;'>Case Number</th><th style='border:1px solid #ddd;'>Operation</th><th style='border:1px solid #ddd;'>Root Cause</th><th style='border:1px solid #ddd;'>Count</th><th style='border:1px solid #ddd;'>First (UTC)</th></tr>");
                foreach (var g in groupedFailures)
                {
                    sb.AppendLine($"<tr><td style='border:1px solid #ddd;'>{E(g.CaseNumber)}</td><td style='border:1px solid #ddd;'>{E(g.Operation)}</td><td style='border:1px solid #ddd;'>{E(g.RootCause)}</td><td style='border:1px solid #ddd;'>{g.Count}</td><td style='border:1px solid #ddd;'>{g.FirstOccurrence:yyyy-MM-dd HH:mm}</td></tr>");
                }
                sb.AppendLine("</table>");
            }

            // Section 4b — Document Ingestion Failures
            sb.AppendLine("<hr/>");
            sb.AppendLine("<h3 style='margin:16px 0 8px;'>Document Ingestion Failures</h3>");
            if (docIngestionFailures.Count == 0)
                sb.AppendLine("<p>None.</p>");
            else
            {
                sb.AppendLine($"<p>Total: <b>{docFailTotal}</b> &nbsp;|&nbsp; Column file_mkyet713 (scene docs): <b>{docFailScene}</b></p>");
                sb.AppendLine("<table style='border-collapse:collapse;border:1px solid #ddd;font-size:13px;' cellpadding='5'>");
                sb.AppendLine("<tr style='background:#f5f5f5;'>"
                    + "<th style='border:1px solid #ddd;'>TikNumber</th>"
                    + "<th style='border:1px solid #ddd;'>TikCounter</th>"
                    + "<th style='border:1px solid #ddd;'>MondayItemId</th>"
                    + "<th style='border:1px solid #ddd;'>ColumnId</th>"
                    + "<th style='border:1px solid #ddd;'>AssetId</th>"
                    + "<th style='border:1px solid #ddd;'>FileName</th>"
                    + "<th style='border:1px solid #ddd;'>Retries</th>"
                    + "<th style='border:1px solid #ddd;'>Status</th>"
                    + "<th style='border:1px solid #ddd;'>LastError</th>"
                    + "<th style='border:1px solid #ddd;'>FirstFailure (UTC)</th>"
                    + "<th style='border:1px solid #ddd;'>LastFailure (UTC)</th>"
                    + "</tr>");
                foreach (var d in docIngestionFailures)
                {
                    var errSnippet = (d.ErrorMessage ?? "").Length > 120 ? d.ErrorMessage![..120] + "…" : d.ErrorMessage ?? "";
                    sb.AppendLine("<tr>"
                        + $"<td style='border:1px solid #ddd;'>{E(d.TikVisualID ?? "")}</td>"
                        + $"<td style='border:1px solid #ddd;'>{d.TikCounter}</td>"
                        + $"<td style='border:1px solid #ddd;'>{d.MondayQuestionnaireItemId}</td>"
                        + $"<td style='border:1px solid #ddd;'>{E(d.ColumnId)}</td>"
                        + $"<td style='border:1px solid #ddd;'>{E(d.AssetId)}</td>"
                        + $"<td style='border:1px solid #ddd;'>{E(d.OriginalFileName)}</td>"
                        + $"<td style='border:1px solid #ddd;'>{d.RetryCount}</td>"
                        + $"<td style='border:1px solid #ddd;'>{d.Status}</td>"
                        + $"<td style='border:1px solid #ddd;'>{E(errSnippet)}</td>"
                        + $"<td style='border:1px solid #ddd;'>{d.CreatedAtUtc:yyyy-MM-dd HH:mm}</td>"
                        + $"<td style='border:1px solid #ddd;'>{d.UpdatedAtUtc:yyyy-MM-dd HH:mm}</td>"
                        + "</tr>");
                }
                sb.AppendLine("</table>");
            }

            // Section 4c — Voicenter Call Summaries
            if (voicenterWritten > 0 || voicenterFailed.Count > 0 || lastVcResult != null)
            {
                sb.AppendLine("<hr/>");
                sb.AppendLine("<h3 style='margin:16px 0 8px;'>Voicenter Call Summaries</h3>");
                sb.AppendLine("<table style='border-collapse:collapse; width:400px;'>");
                if (lastVcResult != null)
                {
                    sb.AppendLine($"<tr><td style='padding:4px 12px 4px 0;'>CDR entries fetched (last run)</td><td style='padding:4px;'>{lastVcResult.Fetched}</td></tr>");
                    sb.AppendLine($"<tr><td style='padding:4px 12px 4px 0;'>Call details fetched</td><td style='padding:4px;'>{lastVcResult.DetailsFetched}</td></tr>");
                    sb.AppendLine($"<tr><td style='padding:4px 12px 4px 0;'>Skipped no AI</td><td style='padding:4px;'>{lastVcResult.SkippedNoAi}</td></tr>");
                    sb.AppendLine($"<tr><td style='padding:4px 12px 4px 0;'>Skipped no match</td><td style='padding:4px;'>{lastVcResult.SkippedNoMatch}</td></tr>");
                    sb.AppendLine($"<tr><td style='padding:4px 12px 4px 0;'>Skipped duplicate</td><td style='padding:4px;'>{lastVcResult.SkippedDuplicate}</td></tr>");
                }
                sb.AppendLine($"<tr><td style='padding:4px 12px 4px 0;'>Annexes written (yesterday)</td><td style='padding:4px;'>{voicenterWritten}</td></tr>");
                var vcFailStyle = voicenterFailed.Count > 0 ? "color:red;font-weight:bold;" : "";
                sb.AppendLine($"<tr><td style='padding:4px 12px 4px 0;'>Failed writes (yesterday)</td><td style='padding:4px;{vcFailStyle}'>{voicenterFailed.Count}</td></tr>");
                sb.AppendLine("</table>");

                if (voicenterFailed.Count > 0)
                {
                    var sample = voicenterFailed.Take(10).ToList();
                    sb.AppendLine($"<p style='margin-top:8px;'>Sample failed CallIDs ({sample.Count} of {voicenterFailed.Count}):</p>");
                    sb.AppendLine("<ul style='font-size:13px;'>");
                    foreach (var f in sample)
                    {
                        var info = f.ErrorMessage ?? "";
                        var snippet = info.Length > 100 ? info[..100] + "…" : info;
                        sb.AppendLine($"<li>SrcId={f.SourceItemId}, Tik={E(f.TikVisualId ?? "")}: {E(snippet)}</li>");
                    }
                    sb.AppendLine("</ul>");
                }
            }

            // Section 5 — System Notes (optional)
            var notes = new List<string>();
            if (circuitBreakerTripped) notes.Add("Circuit breaker tripped during a sync run yesterday.");
            if (highFailureNote) notes.Add("Unusually high failure count yesterday; review failures above.");
            if (notes.Count > 0)
            {
                sb.AppendLine("<hr/>");
                sb.AppendLine("<h3 style='margin:16px 0 8px;'>System Notes</h3>");
                sb.AppendLine("<ul>");
                foreach (var n in notes)
                    sb.AppendLine($"<li>{E(n)}</li>");
                sb.AppendLine("</ul>");
            }

            sb.AppendLine("<br/><small>Generated by ODMON Worker email monitor.</small>");
            sb.AppendLine("</body></html>");
            return sb.ToString();
        }

        // ================================================================
        // Digest — suppressed alerts summary (every N minutes during incidents)
        // ================================================================

        private async Task TrySendDigestAsync(CancellationToken ct)
        {
            try
            {
                if (!_config.GetValue<bool>("Email:Enabled", false))
                    return;

                var intervalMinutes = _config.GetValue<int>("Email:DigestIntervalMinutes", 15);
                if (DateTime.UtcNow - _lastDigestUtc < TimeSpan.FromMinutes(intervalMinutes))
                    return;

                var suppressed = _emailNotifier.GetSuppressedAlerts();
                if (suppressed.Count == 0)
                    return;

                _lastDigestUtc = DateTime.UtcNow;

                var subject = $"Incident Digest – {suppressed.Count} suppressed alert types";
                var body = BuildDigestHtml(suppressed);

                await _emailNotifier.SendDigestAsync(subject, body, ct);
                _emailNotifier.ClearSuppressedCounts();

                _logger.LogInformation(
                    "DIGEST SENT | SuppressedTypes={Count}, TotalSuppressed={Total}",
                    suppressed.Count, suppressed.Sum(s => s.SuppressedCount));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Failed to send digest email.");
            }
        }

        private static string BuildDigestHtml(List<EmailNotifier.SuppressedAlertInfo> suppressed)
        {
            var sb = new StringBuilder();
            sb.AppendLine("<html><body style='font-family:Arial,sans-serif;'>");
            sb.AppendLine("<h2>ODMON Incident Digest</h2>");
            sb.AppendLine("<p>The following alert fingerprints were suppressed (dedup/rate limit):</p>");
            sb.AppendLine("<table style='border-collapse:collapse;' border='1' cellpadding='4'>");
            sb.AppendLine("<tr style='background:#f0f0f0;'><th>Fingerprint</th><th>Occurrences</th><th>Suppressed</th><th>First Seen (UTC)</th><th>Last Seen (UTC)</th></tr>");

            foreach (var s in suppressed.OrderByDescending(x => x.SuppressedCount))
            {
                sb.AppendLine($"<tr><td><code>{s.Fingerprint}</code></td><td>{s.OccurrenceCount}</td><td>{s.SuppressedCount}</td><td>{s.FirstSeenUtc:yyyy-MM-dd HH:mm}</td><td>{s.LastSeenUtc:yyyy-MM-dd HH:mm}</td></tr>");
            }

            sb.AppendLine("</table>");
            sb.AppendLine("<br/><small>Generated by ODMON Worker email monitor.</small>");
            sb.AppendLine("</body></html>");
            return sb.ToString();
        }

    }
}
