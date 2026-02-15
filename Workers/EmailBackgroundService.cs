using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Odmon.Worker.Data;
using Odmon.Worker.Models;
using Odmon.Worker.Services;

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
            if (!_config.GetValue<bool>("Email:Enabled", false))
            {
                _logger.LogInformation("Email monitoring is DISABLED (Email:Enabled=false). EmailBackgroundService will idle.");
                // Keep alive but idle
                try { await Task.Delay(Timeout.Infinite, stoppingToken); } catch (OperationCanceledException) { }
                return;
            }

            _logger.LogInformation("EmailBackgroundService started. Queue processing + daily summary + digest active.");

            // Process queue and run periodic tasks concurrently
            var queueTask = ProcessQueueAsync(stoppingToken);
            var periodicTask = RunPeriodicTasksAsync(stoppingToken);

            await Task.WhenAll(queueTask, periodicTask);
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

                // Send if: past target time today AND haven't sent for today yet
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

                // Query metrics for the last 24 hours
                var since = DateTime.UtcNow.AddHours(-24);
                var metrics = await db.SyncRunMetrics
                    .AsNoTracking()
                    .Where(m => m.StartedAtUtc >= since)
                    .ToListAsync(ct);

                // Count new mappings created today (UTC-based approximation)
                var newMappings = await db.MondayItemMappings
                    .AsNoTracking()
                    .Where(m => m.CreatedAtUtc >= since)
                    .CountAsync(ct);

                // Count failures today
                var failures = await db.SyncFailures
                    .AsNoTracking()
                    .Where(f => f.OccurredAtUtc >= since)
                    .CountAsync(ct);

                var circuitBreakerIncidents = metrics.Count(m => m.CircuitBreakerTripped);
                var totalRuns = metrics.Count;
                var totalCreated = metrics.Sum(m => m.BootstrapCreated);
                var totalUpdated = metrics.Sum(m => m.Updated);
                var totalSkipped = metrics.Sum(m => m.SkippedNoChange);
                var totalCooling = metrics.Sum(m => m.CoolingFilteredOut);
                var totalFailed = metrics.Sum(m => m.Failed) + metrics.Sum(m => m.BootstrapFailed);
                var avgDuration = totalRuns > 0 ? metrics.Average(m => m.DurationMs) : 0;
                var maxDuration = totalRuns > 0 ? metrics.Max(m => m.DurationMs) : 0;

                var subject = $"Daily Summary – {israelDate:yyyy-MM-dd}";
                var body = BuildDailySummaryHtml(
                    israelDate, totalRuns, newMappings, totalCreated, totalUpdated,
                    totalSkipped, totalCooling, totalFailed, failures,
                    circuitBreakerIncidents, avgDuration, maxDuration);

                await _emailNotifier.SendDailySummaryAsync(subject, body, ct);

                _logger.LogInformation(
                    "DAILY SUMMARY SENT | Date={Date}, Runs={Runs}, Created={Created}, Updated={Updated}, Failed={Failed}",
                    israelDate, totalRuns, totalCreated, totalUpdated, totalFailed);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Failed to send daily summary email.");
            }
        }

        private static string BuildDailySummaryHtml(
            DateOnly date, int runs, int newMappings, int created, int updated,
            int skipped, int cooling, int failed, int syncFailures,
            int cbIncidents, double avgDurationMs, int maxDurationMs)
        {
            var sb = new StringBuilder();
            sb.AppendLine("<html><body style='font-family:Arial,sans-serif;'>");
            sb.AppendLine($"<h2>ODMON Daily Summary — {date:yyyy-MM-dd}</h2>");
            sb.AppendLine("<table style='border-collapse:collapse; width:400px;'>");

            void Row(string label, object value, bool warn = false)
            {
                var color = warn ? "color:red;font-weight:bold;" : "";
                sb.AppendLine($"<tr><td style='padding:4px 12px 4px 0;'>{label}</td><td style='padding:4px;{color}'>{value}</td></tr>");
            }

            Row("Sync runs", runs);
            Row("New Monday items (mappings)", newMappings);
            Row("Bootstrap created", created);
            Row("Updated", updated);
            Row("Skipped (no change)", skipped);
            Row("Cooling filtered", cooling);
            Row("Total failures (run-level)", failed, failed > 0);
            Row("SyncFailures (persisted)", syncFailures, syncFailures > 0);
            Row("Circuit breaker incidents", cbIncidents, cbIncidents > 0);
            Row("Avg run duration", $"{avgDurationMs:F0} ms");
            Row("Max run duration", $"{maxDurationMs} ms");

            sb.AppendLine("</table>");
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
