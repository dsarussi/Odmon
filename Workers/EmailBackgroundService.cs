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
                static List<DailySummaryFailureGroup> BuildFailureGroups(IEnumerable<SyncFailure> failures)
                {
                    return failures
                        .GroupBy(f => (
                            CaseNumber: f.TikNumber ?? "",
                            Operation: f.Operation ?? "",
                            RootCause: f.ErrorType ?? "Unknown",
                            Message: NormalizeErrorMessage(f.ErrorMessage)))
                        .Select(g => new DailySummaryFailureGroup(
                            g.Key.CaseNumber,
                            g.Key.Operation,
                            g.Key.RootCause,
                            g.Key.Message,
                            g.Count(),
                            g.Min(x => x.OccurredAtUtc),
                            g.Max(x => x.OccurredAtUtc)))
                        .OrderByDescending(x => x.Count)
                        .ThenBy(x => x.FirstOccurrenceUtc)
                        .ToList();
                }

                var categorizedFailures = allFailuresInWindow
                    .Select(f => new { Failure = f, Category = FailureClassifier.Classify(f.ErrorType, f.Operation) })
                    .ToList();
                var realFailures = categorizedFailures
                    .Where(x => x.Category is FailureCategory.Critical or FailureCategory.Operational)
                    .Select(x => x.Failure)
                    .ToList();
                var groupedFailures = BuildFailureGroups(realFailures);
                var knownDataIssueFailures = BuildFailureGroups(categorizedFailures
                    .Where(x => x.Category == FailureCategory.KnownDataIssue)
                    .Select(x => x.Failure));
                var skippedExpectedFailures = BuildFailureGroups(categorizedFailures
                    .Where(x => x.Category == FailureCategory.SkippedExpected)
                    .Select(x => x.Failure));
                var realFailureCount = groupedFailures.Count;

                // Document ingestion success / failure counts (yesterday window)
                var docIngestionSucceeded = await db.MondayDocumentImports
                    .AsNoTracking()
                    .CountAsync(d => d.Status == DocumentImportStatus.Success
                                     && d.UpdatedAtUtc >= startUtc && d.UpdatedAtUtc <= endUtc, ct);
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

                // Voicenter weekly CallHistoryDetail usage (single quota line in summary)
                var weekStartUtc = VoicenterUsageTracker.GetWeekStartUtc(DateTime.UtcNow);
                var weeklyDetailReq = await db.VoicenterApiRequestLogs.AsNoTracking()
                    .CountAsync(r => r.EndpointType == VoicenterEndpointType.CallHistoryDetail
                                     && r.WeekStartUtc == weekStartUtc, ct);
                var quotaExceededRecently = await db.VoicenterApiRequestLogs.AsNoTracking()
                    .AnyAsync(r => r.QuotaExceeded && r.CreatedAtUtc >= startUtc && r.CreatedAtUtc <= endUtc, ct);

                var runMetricsInWindow = await db.SyncRunMetrics
                    .AsNoTracking()
                    .Where(m => m.StartedAtUtc >= startUtc && m.StartedAtUtc <= endUtc)
                    .ToListAsync(ct);
                var circuitBreakerTripped = runMetricsInWindow.Any(m => m.CircuitBreakerTripped);
                var totalRunFailures = runMetricsInWindow.Sum(m => m.Failed + m.BootstrapFailed);
                var highFailureNote = realFailureCount > 10 || totalRunFailures > 15;

                var subject = $"ODMON Daily Summary — {yesterdayIsrael:yyyy-MM-dd}";
                var knownBlockedTikVisualIds = _config
                    .GetSection("Email:DailySummary:KnownBlockedTikVisualIds")
                    .Get<string[]>() ?? Array.Empty<string>();
                var configuredVoicenterHardLimit = _config.GetValue<int?>("VoicenterCallSummaries:WeeklyUsageHardLimit");
                var body = BuildDailySummaryHtml(
                    yesterdayIsrael,
                    casesCreatedCount,
                    hearingsSyncedCount,
                    itemsUpdatedCount,
                    realFailureCount,
                    groupedFailures,
                    docIngestionSucceeded,
                    docIngestionFailures,
                    voicenterWritten, voicenterFailed, lastVcResult,
                    weeklyDetailReq, quotaExceededRecently,
                    circuitBreakerTripped,
                    highFailureNote,
                    new DailySummaryRenderOptions(
                        knownBlockedTikVisualIds,
                        configuredVoicenterHardLimit),
                    knownDataIssueFailures,
                    skippedExpectedFailures);

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

        internal static string BuildDailySummaryHtml(
            DateOnly date,
            int casesCreatedCount,
            int hearingsSyncedCount,
            int itemsUpdatedCount,
            int realFailureCount,
            List<DailySummaryFailureGroup> groupedFailures,
            int docIngestionSucceeded,
            List<MondayDocumentImport> docIngestionFailures,
            int voicenterWritten, List<NispahWriteLog> voicenterFailed, Voicenter.VoicenterRunResult? lastVcResult,
            int weeklyDetailReq, bool quotaExceededRecently,
            bool circuitBreakerTripped,
            bool highFailureNote,
            DailySummaryRenderOptions? options = null,
            List<DailySummaryFailureGroup>? knownDataIssueFailures = null,
            List<DailySummaryFailureGroup>? skippedExpectedFailures = null)
        {
            static string E(string s) => System.Net.WebUtility.HtmlEncode(s ?? string.Empty);
            static string Cell(string? s) => E(string.IsNullOrWhiteSpace(s) ? "N/A" : s.Trim());

            var docFailTotal = docIngestionFailures.Count;
            var docGroups = BuildDocumentFailureGroups(docIngestionFailures, options);
            var actionableDocGroups = docGroups.Where(g => g.Category == DocumentFailureCategory.Actionable).Take(10).ToList();
            var staleDocGroups = docGroups.Where(g => g.Category == DocumentFailureCategory.KnownStale).ToList();
            var invalidUserFileGroups = docGroups.Where(g => g.Category == DocumentFailureCategory.InvalidUserFile).ToList();
            var externalTransientGroups = docGroups.Where(g => g.Category == DocumentFailureCategory.ExternalTransient).ToList();
            var knownBlockedGroups = docGroups.Where(g => g.Category == DocumentFailureCategory.KnownBlocked).ToList();
            var knownDataIssueGroups = docGroups.Where(g => g.Category == DocumentFailureCategory.KnownDataIssue).ToList();
            var actionableDocCount = actionableDocGroups.Sum(g => g.Count);
            var staleDocCount = staleDocGroups.Sum(g => g.Count);
            var invalidUserFileCount = invalidUserFileGroups.Sum(g => g.Count);
            var externalTransientCount = externalTransientGroups.Sum(g => g.Count);
            var knownBlockedCount = knownBlockedGroups.Sum(g => g.Count);
            var knownDataIssueCount = knownDataIssueGroups.Sum(g => g.Count);

            // ---- Compute System Status from real signals ----
            // Critical: circuit breaker tripped, OR high failure burst
            // Warning : real failures > 0, doc ingestion failures > 0, voicenter quota exceeded,
            //           voicenter failed writes > 0, OR (CDR fetched but 0 details + quota exceeded recently)
            // OK      : everything else
            var voicenterQuotaExceeded = quotaExceededRecently
                || (lastVcResult != null && lastVcResult.SkippedDueToQuotaExceeded > 0)
                || (lastVcResult != null && lastVcResult.ApiLimitExceeded);

            var critical = circuitBreakerTripped || highFailureNote;
            var warning =
                realFailureCount > 0
                || actionableDocCount > 0
                || staleDocCount > 0
                || knownBlockedCount > 0
                || knownDataIssueCount > 0
                || voicenterFailed.Count > 0
                || voicenterQuotaExceeded
                || (lastVcResult != null && lastVcResult.DetailFetchFailed > 0);

            string statusLabel;
            string statusColor;
            string statusDetail;
            if (critical)
            {
                statusLabel = "Critical";
                statusColor = "#c0392b";
                statusDetail = circuitBreakerTripped
                    ? "Circuit breaker tripped during a sync run yesterday."
                    : "Unusually high failure count yesterday; review issues below.";
            }
            else if (warning)
            {
                statusLabel = "Warning";
                statusColor = "#b8860b";
                var reasons = new List<string>();
                if (voicenterQuotaExceeded) reasons.Add("Voicenter quota exceeded");
                if (realFailureCount > 0) reasons.Add($"{realFailureCount} sync failure(s)");
                if (actionableDocCount > 0) reasons.Add($"{actionableDocCount} actionable document ingestion failure(s)");
                if (staleDocCount > 0) reasons.Add($"{staleDocCount} stale document timeout(s) grouped");
                if (invalidUserFileCount > 0) reasons.Add($"{invalidUserFileCount} invalid user file issue(s)");
                if (externalTransientCount > 0) reasons.Add($"{externalTransientCount} external transient document failure(s)");
                if (knownBlockedCount > 0) reasons.Add($"{knownBlockedCount} known blocked case failure(s)");
                if (knownDataIssueCount > 0) reasons.Add($"{knownDataIssueCount} known data issue(s) still open");
                if (voicenterFailed.Count > 0) reasons.Add($"{voicenterFailed.Count} Voicenter write failure(s)");
                if (lastVcResult != null && lastVcResult.DetailFetchFailed > 0) reasons.Add($"{lastVcResult.DetailFetchFailed} Voicenter detail fetch failure(s)");
                statusDetail = reasons.Count > 0
                    ? string.Join("; ", reasons) + ". Other services completed expected work."
                    : "Non-critical anomalies detected.";
            }
            else
            {
                statusLabel = "OK";
                statusColor = "#27ae60";
                statusDetail = "All enabled services completed expected work.";
            }

            var sb = new StringBuilder();
            sb.AppendLine("<html><body style='font-family:Arial,sans-serif;'>");
            sb.AppendLine($"<h2>ODMON Daily Summary — {date:yyyy-MM-dd}</h2>");
            sb.AppendLine("<p>Previous day (00:00–23:59 Israel time).</p>");

            // ---- System Status ----
            sb.AppendLine("<h3 style='margin:16px 0 8px;'>System Status</h3>");
            sb.AppendLine($"<p style='margin:0 0 12px 0;'><span style='display:inline-block;padding:4px 10px;border-radius:4px;background:{statusColor};color:#fff;font-weight:bold;'>{statusLabel}</span> &nbsp; {E(statusDetail)}</p>");

            // ---- Daily Overview ----
            sb.AppendLine("<h3 style='margin:16px 0 8px;'>Daily Overview</h3>");
            sb.AppendLine("<table style='border-collapse:collapse; width:420px;'>");
            sb.AppendLine($"<tr><td style='padding:4px 12px 4px 0;'>Cases created in Monday yesterday</td><td style='padding:4px;'>{casesCreatedCount}</td></tr>");
            sb.AppendLine($"<tr><td style='padding:4px 12px 4px 0;'>Hearings synced yesterday</td><td style='padding:4px;'>{hearingsSyncedCount}</td></tr>");
            sb.AppendLine($"<tr><td style='padding:4px 12px 4px 0;'>Items updated yesterday</td><td style='padding:4px;'>{itemsUpdatedCount}</td></tr>");
            sb.AppendLine($"<tr><td style='padding:4px 12px 4px 0;'>Document ingestion succeeded</td><td style='padding:4px;'>{docIngestionSucceeded}</td></tr>");
            var docFailStyle = docFailTotal > 0 ? "color:#d35400;font-weight:bold;" : "";
            sb.AppendLine($"<tr><td style='padding:4px 12px 4px 0;'>Document ingestion failures</td><td style='padding:4px;{docFailStyle}'>{docFailTotal}</td></tr>");
            sb.AppendLine($"<tr><td style='padding:4px 12px 4px 0;'>Voicenter annexes written yesterday</td><td style='padding:4px;'>{voicenterWritten}</td></tr>");
            var failStyle = realFailureCount > 0 ? "color:red;font-weight:bold;" : "";
            sb.AppendLine($"<tr><td style='padding:4px 12px 4px 0;'>Real failures yesterday</td><td style='padding:4px;{failStyle}'>{realFailureCount}</td></tr>");
            sb.AppendLine("</table>");

            // ---- Issues Requiring Attention ----
            sb.AppendLine("<hr/>");
            sb.AppendLine("<h3 style='margin:16px 0 8px;'>Issues Requiring Attention</h3>");
            if (groupedFailures.Count == 0)
                sb.AppendLine("<p>None.</p>");
            else
            {
                sb.AppendLine("<table style='border-collapse:collapse;border:1px solid #ddd;' cellpadding='6'>");
                sb.AppendLine("<tr style='background:#f5f5f5;'><th style='border:1px solid #ddd;'>TikNumber</th><th style='border:1px solid #ddd;'>Operation</th><th style='border:1px solid #ddd;'>Exception type</th><th style='border:1px solid #ddd;'>Message / API response</th><th style='border:1px solid #ddd;'>Count</th><th style='border:1px solid #ddd;'>First (UTC)</th><th style='border:1px solid #ddd;'>Last (UTC)</th></tr>");
                foreach (var g in groupedFailures)
                {
                    sb.AppendLine($"<tr><td style='border:1px solid #ddd;'>{Cell(g.CaseNumber)}</td><td style='border:1px solid #ddd;'>{Cell(g.Operation)}</td><td style='border:1px solid #ddd;'>{Cell(g.ExceptionType)}</td><td style='border:1px solid #ddd;'>{Cell(Truncate(g.Message, 500))}</td><td style='border:1px solid #ddd;'>{g.Count}</td><td style='border:1px solid #ddd;'>{g.FirstOccurrenceUtc:yyyy-MM-dd HH:mm}</td><td style='border:1px solid #ddd;'>{g.LastOccurrenceUtc:yyyy-MM-dd HH:mm}</td></tr>");
                }
                sb.AppendLine("</table>");
            }

            AppendFailureGroupSection(sb, "Known Data Issues", knownDataIssueFailures ?? [], Cell, E);
            AppendFailureGroupSection(sb, "Skipped Expected", skippedExpectedFailures ?? [], Cell, E);

            // ---- Warnings / Anomalies (only when something is worth noting) ----
            var anomalies = new List<string>();
            if (voicenterQuotaExceeded)
                anomalies.Add("Voicenter quota exceeded; backfill should wait until quota resets.");
            if (lastVcResult != null && lastVcResult.Fetched > 0 && lastVcResult.DetailsFetched == 0 && voicenterQuotaExceeded)
                anomalies.Add("CDR entries fetched but no call details fetched due to quota.");
            if (lastVcResult != null && lastVcResult.DetailFetchFailed > 0)
                anomalies.Add($"{lastVcResult.DetailFetchFailed} Voicenter detail fetch failure(s) — see incident alerts.");
            if (circuitBreakerTripped)
                anomalies.Add("Worker circuit breaker tripped — incident alert was sent.");
            if (highFailureNote)
                anomalies.Add("Unusually high failure count yesterday; review issues above.");
            if (actionableDocCount > 0)
                anomalies.Add($"Actionable document ingestion failure count > 0 ({actionableDocCount}). See section below.");
            if (staleDocCount > 0)
                anomalies.Add($"{staleDocCount} known/stale document timeout(s) were grouped separately.");
            if (knownDataIssueCount > 0)
                anomalies.Add($"{knownDataIssueCount} known data issue(s) remain open and visible.");

            if (anomalies.Count > 0)
            {
                sb.AppendLine("<hr/>");
                sb.AppendLine("<h3 style='margin:16px 0 8px;'>Warnings / Anomalies</h3>");
                sb.AppendLine("<ul style='margin:0 0 0 18px;padding:0;'>");
                foreach (var a in anomalies)
                    sb.AppendLine($"<li>{E(a)}</li>");
                sb.AppendLine("</ul>");
            }

            // ---- Document Ingestion Failures (concise) ----
            sb.AppendLine("<hr/>");
            sb.AppendLine("<h3 style='margin:16px 0 8px;'>Document Ingestion Failures</h3>");
            if (docIngestionFailures.Count == 0)
                sb.AppendLine("<p>None.</p>");
            else
            {
                sb.AppendLine($"<p>Total: <b>{docFailTotal}</b> &nbsp;|&nbsp; Actionable shown: <b>{actionableDocCount}</b> &nbsp;|&nbsp; Known/stale: <b>{staleDocCount}</b> &nbsp;|&nbsp; Known blocked: <b>{knownBlockedCount}</b> &nbsp;|&nbsp; Known data issues: <b>{knownDataIssueCount}</b></p>");
                AppendDocumentFailureGroupTable(sb, "Actionable document ingestion failures", actionableDocGroups, Cell, E);
                AppendDocumentFailureGroupTable(sb, "Known/Stale document ingestion failures", staleDocGroups, Cell, E);
                AppendDocumentFailureGroupTable(sb, "Invalid user file document ingestion failures", invalidUserFileGroups, Cell, E);
                AppendDocumentFailureGroupTable(sb, "External transient document ingestion failures", externalTransientGroups, Cell, E);
                AppendDocumentFailureGroupTable(sb, "Known blocked cases", knownBlockedGroups, Cell, E);
                AppendDocumentFailureGroupTable(sb, "Known data issues", knownDataIssueGroups, Cell, E);
                if (docIngestionFailures.Count < 0)
                {
                sb.AppendLine("<table style='border-collapse:collapse;border:1px solid #ddd;font-size:13px;' cellpadding='5'>");
                sb.AppendLine("<tr style='background:#f5f5f5;'>"
                    + "<th style='border:1px solid #ddd;'>TikNumber</th>"
                    + "<th style='border:1px solid #ddd;'>ColumnId</th>"
                    + "<th style='border:1px solid #ddd;'>FileName</th>"
                    + "<th style='border:1px solid #ddd;'>Retries</th>"
                    + "<th style='border:1px solid #ddd;'>LastError</th>"
                    + "<th style='border:1px solid #ddd;'>LastFailure (UTC)</th>"
                    + "</tr>");
                foreach (var d in docIngestionFailures)
                {
                    var errSnippet = (d.ErrorMessage ?? "").Length > 120 ? d.ErrorMessage![..120] + "…" : d.ErrorMessage ?? "";
                    sb.AppendLine("<tr>"
                        + $"<td style='border:1px solid #ddd;'>{E(d.TikVisualID ?? "")}</td>"
                        + $"<td style='border:1px solid #ddd;'>{E(d.ColumnId)}</td>"
                        + $"<td style='border:1px solid #ddd;'>{E(d.OriginalFileName)}</td>"
                        + $"<td style='border:1px solid #ddd;'>{d.RetryCount}</td>"
                        + $"<td style='border:1px solid #ddd;'>{E(errSnippet)}</td>"
                        + $"<td style='border:1px solid #ddd;'>{d.UpdatedAtUtc:yyyy-MM-dd HH:mm}</td>"
                        + "</tr>");
                }
                sb.AppendLine("</table>");
                }
            }

            // ---- Voicenter Call Summaries (compact) ----
            sb.AppendLine("<hr/>");
            sb.AppendLine("<h3 style='margin:16px 0 8px;'>Voicenter Call Summaries</h3>");

            string vcStatus;
            string vcStatusStyle = "";
            if (voicenterQuotaExceeded)
            {
                vcStatus = "Quota exceeded";
                vcStatusStyle = "color:#c0392b;font-weight:bold;";
            }
            else if (lastVcResult == null)
            {
                vcStatus = "No run data available";
                vcStatusStyle = "color:#7f8c8d;";
            }
            else if (lastVcResult.DetailFetchFailed > 0 || voicenterFailed.Count > 0)
            {
                vcStatus = "Errors during run";
                vcStatusStyle = "color:#c0392b;font-weight:bold;";
            }
            else if (lastVcResult.BackfillMode)
            {
                vcStatus = $"Backfill mode ({(lastVcResult.BackfillDryRun ? "DryRun" : "Live")})";
                vcStatusStyle = "color:#2980b9;font-weight:bold;";
            }
            else
            {
                vcStatus = "OK";
                vcStatusStyle = "color:#27ae60;font-weight:bold;";
            }

            var vcFetched = lastVcResult?.Fetched ?? 0;
            var vcDetails = lastVcResult?.DetailsFetched ?? 0;
            var vcSkippedNoAi = lastVcResult?.SkippedNoAi ?? 0;
            var vcSkippedNoMatch = lastVcResult?.SkippedNoMatch ?? 0;
            var vcSkippedDup = lastVcResult?.SkippedDuplicate ?? 0;
            var vcSkippedQuota = lastVcResult?.SkippedDueToQuotaExceeded ?? 0;
            var configuredHardLimit = options?.VoicenterWeeklyUsageHardLimit;
            var vcWeeklyLimit = configuredHardLimit.GetValueOrDefault(400);
            var vcWeeklyLimitConfigured = configuredHardLimit is > 0;
            if (!vcWeeklyLimitConfigured)
                vcWeeklyLimit = 0;
            var vcWeeklyWarn = lastVcResult?.WeeklyCallHistoryDetailWarningThreshold ?? 0;
            var vcUsageStyle = vcWeeklyWarn > 0 && weeklyDetailReq >= vcWeeklyWarn ? "color:#b8860b;font-weight:bold;" : "";

            sb.AppendLine("<table style='border-collapse:collapse; width:480px;'>");
            sb.AppendLine($"<tr><td style='padding:4px 12px 4px 0;'>CDR entries fetched (last run)</td><td style='padding:4px;'>{vcFetched}</td></tr>");
            sb.AppendLine($"<tr><td style='padding:4px 12px 4px 0;'>Call details fetched</td><td style='padding:4px;'>{vcDetails}</td></tr>");
            sb.AppendLine($"<tr><td style='padding:4px 12px 4px 0;'>Skipped no AI</td><td style='padding:4px;'>{vcSkippedNoAi}</td></tr>");
            sb.AppendLine($"<tr><td style='padding:4px 12px 4px 0;'>Skipped no match</td><td style='padding:4px;'>{vcSkippedNoMatch}</td></tr>");
            sb.AppendLine($"<tr><td style='padding:4px 12px 4px 0;'>Skipped duplicate</td><td style='padding:4px;'>{vcSkippedDup}</td></tr>");
            sb.AppendLine($"<tr><td style='padding:4px 12px 4px 0;'>Annexes written (yesterday)</td><td style='padding:4px;'>{voicenterWritten}</td></tr>");
            var vcFailStyle = voicenterFailed.Count > 0 ? "color:red;font-weight:bold;" : "";
            sb.AppendLine($"<tr><td style='padding:4px 12px 4px 0;'>Failed writes (yesterday)</td><td style='padding:4px;{vcFailStyle}'>{voicenterFailed.Count}</td></tr>");
            sb.AppendLine($"<tr><td style='padding:4px 12px 4px 0;'>Status</td><td style='padding:4px;{vcStatusStyle}'>{E(vcStatus)}</td></tr>");
            sb.AppendLine($"<tr><td style='padding:4px 12px 4px 0;'>Weekly CallHistoryDetail usage</td><td style='padding:4px;{vcUsageStyle}'>{weeklyDetailReq} / {(vcWeeklyLimitConfigured ? vcWeeklyLimit.ToString() : "not configured")}</td></tr>");
            if (!vcWeeklyLimitConfigured)
            {
                sb.AppendLine("<tr><td style='padding:4px 12px 4px 0;'>Voicenter quota config warning</td><td style='padding:4px;color:#b8860b;'>VoicenterCallSummaries:WeeklyUsageHardLimit is missing or zero; expected default is 400.</td></tr>");
            }
            if (vcSkippedQuota > 0)
            {
                sb.AppendLine($"<tr><td style='padding:4px 12px 4px 0;'>Skipped due to quota exceeded</td><td style='padding:4px;color:#c0392b;'>{vcSkippedQuota}</td></tr>");
            }
            sb.AppendLine("</table>");

            sb.AppendLine("<br/><small>Generated by ODMON Worker email monitor.</small>");
            sb.AppendLine("</body></html>");
            return sb.ToString();
        }

        internal sealed record DailySummaryRenderOptions(
            IReadOnlyCollection<string> KnownBlockedTikVisualIds,
            int? VoicenterWeeklyUsageHardLimit);

        internal sealed record DailySummaryFailureGroup(
            string CaseNumber,
            string Operation,
            string ExceptionType,
            string Message,
            int Count,
            DateTime FirstOccurrenceUtc,
            DateTime LastOccurrenceUtc);

        private enum DocumentFailureCategory
        {
            Actionable,
            KnownStale,
            InvalidUserFile,
            ExternalTransient,
            KnownBlocked,
            KnownDataIssue
        }

        private sealed record DocumentFailureGroup(
            DocumentFailureCategory Category,
            string TikVisualId,
            string ColumnId,
            string NormalizedError,
            int Count,
            DateTime FirstFailureUtc,
            DateTime LastFailureUtc,
            int MaxRetryCount,
            IReadOnlyList<string> SampleFileNames);

        private static List<DocumentFailureGroup> BuildDocumentFailureGroups(
            IReadOnlyCollection<MondayDocumentImport> failures,
            DailySummaryRenderOptions? options)
        {
            var knownBlocked = new HashSet<string>(
                options?.KnownBlockedTikVisualIds ?? Array.Empty<string>(),
                StringComparer.OrdinalIgnoreCase);

            return failures
                .Select(f => new
                {
                    Failure = f,
                    Tik = string.IsNullOrWhiteSpace(f.TikVisualID) ? "N/A" : f.TikVisualID.Trim(),
                    Column = string.IsNullOrWhiteSpace(f.ColumnId) ? "N/A" : f.ColumnId.Trim(),
                    Error = NormalizeErrorMessage(f.ErrorMessage)
                })
                .GroupBy(x => new
                {
                    x.Tik,
                    x.Column,
                    x.Error,
                    Category = ClassifyDocumentFailure(x.Tik, x.Column, x.Error, knownBlocked)
                })
                .Select(g => new DocumentFailureGroup(
                    g.Key.Category,
                    g.Key.Tik,
                    g.Key.Column,
                    g.Key.Error,
                    g.Count(),
                    g.Min(x => x.Failure.CreatedAtUtc),
                    g.Max(x => x.Failure.UpdatedAtUtc),
                    g.Max(x => x.Failure.RetryCount),
                    g.Select(x => x.Failure.OriginalFileName)
                        .Where(name => !string.IsNullOrWhiteSpace(name))
                        .Select(name => name.Trim())
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .Take(3)
                        .ToList()))
                .OrderByDescending(g => g.LastFailureUtc)
                .ToList();
        }

        private static DocumentFailureCategory ClassifyDocumentFailure(
            string tikVisualId,
            string columnId,
            string normalizedError,
            HashSet<string> knownBlocked)
        {
            if (knownBlocked.Contains(tikVisualId))
                return DocumentFailureCategory.KnownBlocked;

            if (tikVisualId.StartsWith("21/", StringComparison.OrdinalIgnoreCase) ||
                tikVisualId.StartsWith("21\\", StringComparison.OrdinalIgnoreCase))
                return DocumentFailureCategory.KnownDataIssue;

            if (string.Equals(columnId, "file_mm1bvngc", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(normalizedError, "Timeout: 2h passed since status ready, no file", StringComparison.OrdinalIgnoreCase))
                return DocumentFailureCategory.KnownStale;

            if (normalizedError.Contains("Max retry", StringComparison.OrdinalIgnoreCase) ||
                normalizedError.Contains("max retries", StringComparison.OrdinalIgnoreCase))
                return DocumentFailureCategory.KnownStale;

            if (normalizedError.Contains("UNSUPPORTED_EXTENSION", StringComparison.OrdinalIgnoreCase) ||
                normalizedError.Contains("DENYLIST_EXTENSION", StringComparison.OrdinalIgnoreCase) ||
                normalizedError.Contains("FILE_TOO_LARGE", StringComparison.OrdinalIgnoreCase) ||
                normalizedError.Contains("InvalidExtension", StringComparison.OrdinalIgnoreCase))
                return DocumentFailureCategory.InvalidUserFile;

            if (normalizedError.Contains("HTTP 503", StringComparison.OrdinalIgnoreCase) ||
                normalizedError.Contains("503", StringComparison.OrdinalIgnoreCase))
                return DocumentFailureCategory.ExternalTransient;

            return DocumentFailureCategory.Actionable;
        }

        private static void AppendDocumentFailureGroupTable(
            StringBuilder sb,
            string title,
            IReadOnlyCollection<DocumentFailureGroup> groups,
            Func<string?, string> cell,
            Func<string, string> encode)
        {
            if (groups.Count == 0)
                return;

            sb.AppendLine($"<h4 style='margin:12px 0 6px;'>{encode(title)}</h4>");
            sb.AppendLine("<table style='border-collapse:collapse;border:1px solid #ddd;font-size:13px;' cellpadding='5'>");
            sb.AppendLine("<tr style='background:#f5f5f5;'>"
                + "<th style='border:1px solid #ddd;'>TikNumber / TikVisualID</th>"
                + "<th style='border:1px solid #ddd;'>ColumnId</th>"
                + "<th style='border:1px solid #ddd;'>Count</th>"
                + "<th style='border:1px solid #ddd;'>FirstFailureUtc</th>"
                + "<th style='border:1px solid #ddd;'>LastFailureUtc</th>"
                + "<th style='border:1px solid #ddd;'>SampleFileNames</th>"
                + "<th style='border:1px solid #ddd;'>RetryCount</th>"
                + "<th style='border:1px solid #ddd;'>LastError</th>"
                + "</tr>");

            foreach (var g in groups)
            {
                var sampleFiles = g.SampleFileNames.Count == 0
                    ? "N/A"
                    : string.Join(", ", g.SampleFileNames);

                sb.AppendLine("<tr>"
                    + $"<td style='border:1px solid #ddd;'>{cell(g.TikVisualId)}</td>"
                    + $"<td style='border:1px solid #ddd;'>{cell(g.ColumnId)}</td>"
                    + $"<td style='border:1px solid #ddd;'>{g.Count}</td>"
                    + $"<td style='border:1px solid #ddd;'>{g.FirstFailureUtc:yyyy-MM-dd HH:mm}</td>"
                    + $"<td style='border:1px solid #ddd;'>{g.LastFailureUtc:yyyy-MM-dd HH:mm}</td>"
                    + $"<td style='border:1px solid #ddd;'>{cell(sampleFiles)}</td>"
                    + $"<td style='border:1px solid #ddd;'>{g.MaxRetryCount}</td>"
                    + $"<td style='border:1px solid #ddd;'>{cell(Truncate(g.NormalizedError, 500))}</td>"
                    + "</tr>");
            }

            sb.AppendLine("</table>");
        }

        private static void AppendFailureGroupSection(
            StringBuilder sb,
            string title,
            IReadOnlyCollection<DailySummaryFailureGroup> groups,
            Func<string?, string> cell,
            Func<string, string> encode)
        {
            if (groups.Count == 0)
                return;

            sb.AppendLine($"<h3 style='margin:16px 0 8px;'>{encode(title)}</h3>");
            sb.AppendLine("<table style='border-collapse:collapse;border:1px solid #ddd;' cellpadding='6'>");
            sb.AppendLine("<tr style='background:#f5f5f5;'><th style='border:1px solid #ddd;'>TikNumber</th><th style='border:1px solid #ddd;'>Operation</th><th style='border:1px solid #ddd;'>Classification</th><th style='border:1px solid #ddd;'>Message</th><th style='border:1px solid #ddd;'>Count</th><th style='border:1px solid #ddd;'>First (UTC)</th><th style='border:1px solid #ddd;'>Last (UTC)</th></tr>");
            foreach (var g in groups.Take(20))
            {
                sb.AppendLine($"<tr><td style='border:1px solid #ddd;'>{cell(g.CaseNumber)}</td><td style='border:1px solid #ddd;'>{cell(g.Operation)}</td><td style='border:1px solid #ddd;'>{cell(g.ExceptionType)}</td><td style='border:1px solid #ddd;'>{cell(Truncate(g.Message, 500))}</td><td style='border:1px solid #ddd;'>{g.Count}</td><td style='border:1px solid #ddd;'>{g.FirstOccurrenceUtc:yyyy-MM-dd HH:mm}</td><td style='border:1px solid #ddd;'>{g.LastOccurrenceUtc:yyyy-MM-dd HH:mm}</td></tr>");
            }
            sb.AppendLine("</table>");
        }

        private static string NormalizeErrorMessage(string? message)
        {
            if (string.IsNullOrWhiteSpace(message))
                return "N/A";

            var normalized = string.Join(" ", message.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).Trim();
            if (normalized.Contains("email is not valid", StringComparison.OrdinalIgnoreCase))
                return "Monday email is not valid";
            return normalized;
        }

        private static string Truncate(string? value, int maxLength)
        {
            if (string.IsNullOrWhiteSpace(value))
                return "N/A";

            return value.Length <= maxLength ? value : value[..maxLength] + "...";
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
