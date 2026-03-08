using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Data.SqlClient;
using Odmon.Worker.Data;
using Odmon.Worker.OdcanitAccess;
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
                var odcanitDb = scope.ServiceProvider.GetRequiredService<OdcanitDbContext>();

                var since = DateTime.UtcNow.AddHours(-24);

                // 1) System Activity (business-level)
                var newMappings = await db.MondayItemMappings
                    .AsNoTracking()
                    .Where(m => m.CreatedAtUtc >= since)
                    .CountAsync(ct);

                var updatedToday = await db.MondayItemMappings
                    .AsNoTracking()
                    .Where(m => m.LastSyncFromOdcanitUtc >= since)
                    .CountAsync(ct);

                var runFailures = await db.SyncRunMetrics
                    .AsNoTracking()
                    .Where(m => m.StartedAtUtc >= since)
                    .SumAsync(m => m.Failed + m.BootstrapFailed, ct);
                var persistedFailures = await db.SyncFailures
                    .AsNoTracking()
                    .Where(f => f.OccurredAtUtc >= since)
                    .CountAsync(ct);
                var totalFailures = runFailures + persistedFailures;

                // 2) Cases expected to open (today/tomorrow/day-after) — use OdcanitDb connection
                List<UpcomingEligibleRow>? upcomingRows = null;
                try
                {
                    var connStr = odcanitDb.Database.GetConnectionString();
                    if (!string.IsNullOrWhiteSpace(connStr) && !connStr.Contains("CHANGE_ME", StringComparison.OrdinalIgnoreCase) && !connStr.Contains("__USE_SECRET__", StringComparison.Ordinal))
                    {
                        upcomingRows = await UpcomingEligibleSection.LoadAsync(connStr, ct);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Daily summary: Could not load upcoming eligible cases from OdcanitDb.");
                }

                var subject = $"Daily Summary – {israelDate:yyyy-MM-dd}";
                var body = BuildDailySummaryHtml(israelDate, newMappings, updatedToday, totalFailures, upcomingRows ?? new List<UpcomingEligibleRow>());

                if (_config.GetValue<bool>("Email:Enabled", false))
                {
                    await _emailNotifier.SendDailySummaryAsync(subject, body, ct);
                }

                _logger.LogInformation(
                    "DAILY SUMMARY SENT | Date={Date}, NewMappings={New}, Updated={Updated}, Failures={Failures}, Upcoming={Upcoming}",
                    israelDate, newMappings, updatedToday, totalFailures, upcomingRows?.Count ?? 0);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Failed to send daily summary email.");
            }
        }

        private static string BuildDailySummaryHtml(
            DateOnly date, int newMappings, int updatedToday, int totalFailures,
            List<UpcomingEligibleRow> upcomingRows)
        {
            static string E(string s) => System.Net.WebUtility.HtmlEncode(s ?? string.Empty);

            var sb = new StringBuilder();
            sb.AppendLine("<html><body style='font-family:Arial,sans-serif;'>");
            sb.AppendLine($"<h2>ODMON Daily Summary — {date:yyyy-MM-dd}</h2>");

            // 1) System Activity (business-level)
            sb.AppendLine("<h3 style='margin:16px 0 8px;'>פעילות מערכת</h3>");
            sb.AppendLine("<table style='border-collapse:collapse; width:400px;'>");
            sb.AppendLine($"<tr><td style='padding:4px 12px 4px 0;'>פריטים חדשים ב-Monday (היום)</td><td style='padding:4px;'>{newMappings}</td></tr>");
            sb.AppendLine($"<tr><td style='padding:4px 12px 4px 0;'>עודכנו היום</td><td style='padding:4px;'>{updatedToday}</td></tr>");
            var failStyle = totalFailures > 0 ? "color:red;font-weight:bold;" : "";
            sb.AppendLine($"<tr><td style='padding:4px 12px 4px 0;'>סך כשלונות</td><td style='padding:4px;{failStyle}'>{totalFailures}</td></tr>");
            sb.AppendLine("</table>");

            // 2) Cases expected to open (today/tomorrow/day-after)
            sb.AppendLine("<hr/>");
            sb.AppendLine("<div style='direction:rtl;text-align:right;font-family:Arial,sans-serif;'>");
            sb.AppendLine("<h3 style='margin:16px 0 8px;'>תיקים צפויים להיפתח (היום/מחר/מחרתיים)</h3>");
            if (upcomingRows.Count == 0)
            {
                sb.AppendLine("<div>אין תיקים צפויים בטווח הזה.</div>");
            }
            else
            {
                sb.AppendLine("<table style='border-collapse:collapse;width:100%;border:1px solid #ddd;' cellpadding='6'>");
                sb.AppendLine("<thead><tr style='background:#f5f5f5;'>");
                sb.AppendLine("<th style='border:1px solid #ddd;'>מספר תיק</th>");
                sb.AppendLine("<th style='border:1px solid #ddd;'>תאריך יצירה</th>");
                sb.AppendLine("<th style='border:1px solid #ddd;'>זמין לאחר</th>");
                sb.AppendLine("<th style='border:1px solid #ddd;'>תקופה</th>");
                sb.AppendLine("</tr></thead><tbody>");
                foreach (var r in upcomingRows)
                {
                    sb.AppendLine("<tr>");
                    sb.AppendLine($"<td style='border:1px solid #ddd;'>{E(r.TikNumber)}</td>");
                    sb.AppendLine($"<td style='border:1px solid #ddd;'>{r.TsCreateDate:yyyy-MM-dd}</td>");
                    sb.AppendLine($"<td style='border:1px solid #ddd;'>{r.EligibleAfter:yyyy-MM-dd}</td>");
                    sb.AppendLine($"<td style='border:1px solid #ddd;'>{E(r.OpenBucket)}</td>");
                    sb.AppendLine("</tr>");
                }
                sb.AppendLine("</tbody></table>");
            }
            sb.AppendLine("</div>");

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

        // ================================================================
        // NEW: Upcoming eligible cases section (OdmonIntegration/Odlight query)
        // ================================================================

        private sealed record UpcomingEligibleRow(
            int TikCounter,
            string TikNumber,
            DateTime TsCreateDate,
            DateTime EligibleAfter,
            string OpenBucket
        );

        private static class UpcomingEligibleSection
        {
            private const string Sql = @"
SET DATEFIRST 7; -- Sunday=1 ... Friday=6 Saturday=7

DECLARE @TodayIsrael date =
    CONVERT(date, SYSDATETIMEOFFSET() AT TIME ZONE 'Israel Standard Time');

;WITH Src AS
(
    SELECT
        f.TikCounter,
        f.TikNumber,
        CAST(f.tsCreateDate AS date) AS tsCreateDate
    FROM dbo.vwExportToOuterSystems_Files f
),
EligibleCalc AS
(
    SELECT
        s.*,
        EligibleAfter =
            DATEADD(day,
                CASE DATEPART(weekday, s.tsCreateDate)
                    WHEN 1 THEN 3
                    WHEN 2 THEN 3
                    WHEN 3 THEN 5
                    WHEN 4 THEN 5
                    WHEN 5 THEN 5
                    WHEN 6 THEN 4
                    WHEN 7 THEN 3
                END,
                s.tsCreateDate
            )
    FROM Src s
)

SELECT
    TikCounter,
    TikNumber,
    tsCreateDate,
    EligibleAfter,
    CASE
        WHEN EligibleAfter = @TodayIsrael THEN 'TODAY'
        WHEN EligibleAfter = DATEADD(day,1,@TodayIsrael) THEN 'TOMORROW'
        WHEN EligibleAfter = DATEADD(day,2,@TodayIsrael) THEN 'DAY_AFTER'
    END AS OpenBucket
FROM EligibleCalc
WHERE EligibleAfter BETWEEN @TodayIsrael AND DATEADD(day,2,@TodayIsrael)
ORDER BY EligibleAfter, TikCounter;";

            public static async Task<List<UpcomingEligibleRow>> LoadAsync(string odcanitConnectionString, CancellationToken ct)
            {
                var rows = new List<UpcomingEligibleRow>();

                await using var conn = new SqlConnection(odcanitConnectionString);
                await conn.OpenAsync(ct);

                await using var cmd = new SqlCommand(Sql, conn)
                {
                    CommandType = System.Data.CommandType.Text,
                    CommandTimeout = 30
                };

                await using var reader = await cmd.ExecuteReaderAsync(ct);

                int ordTikCounter = reader.GetOrdinal("TikCounter");
                int ordTikNumber = reader.GetOrdinal("TikNumber");
                int ordTsCreateDate = reader.GetOrdinal("tsCreateDate");
                int ordEligibleAfter = reader.GetOrdinal("EligibleAfter");
                int ordOpenBucket = reader.GetOrdinal("OpenBucket");

                while (await reader.ReadAsync(ct))
                {
                    rows.Add(new UpcomingEligibleRow(
                        TikCounter: reader.GetInt32(ordTikCounter),
                        TikNumber: reader.GetString(ordTikNumber),
                        TsCreateDate: reader.GetDateTime(ordTsCreateDate),
                        EligibleAfter: reader.GetDateTime(ordEligibleAfter),
                        OpenBucket: reader.GetString(ordOpenBucket)
                    ));
                }

                return rows;
            }

            public static string ToHtml(IReadOnlyList<UpcomingEligibleRow> rows)
            {
                static string E(string s) => System.Net.WebUtility.HtmlEncode(s ?? string.Empty);

                var sb = new StringBuilder();
                sb.AppendLine("<div style='direction:rtl;text-align:right;font-family:Arial,sans-serif;'>");
                sb.AppendLine("<h3 style='margin:16px 0 8px;'>תיקים צפויים להיפתח (היום/מחר/מחרתיים)</h3>");

                if (rows.Count == 0)
                {
                    sb.AppendLine("<div>אין תיקים צפויים בטווח הזה.</div>");
                    sb.AppendLine("</div>");
                    return sb.ToString();
                }

                sb.AppendLine("<table style='border-collapse:collapse;width:100%;border:1px solid #ddd;' cellpadding='6'>");
                sb.AppendLine("<thead><tr style='background:#f5f5f5;'>");
                sb.AppendLine("<th style='border:1px solid #ddd;'>Bucket</th>");
                sb.AppendLine("<th style='border:1px solid #ddd;'>TikCounter</th>");
                sb.AppendLine("<th style='border:1px solid #ddd;'>TikNumber</th>");
                sb.AppendLine("<th style='border:1px solid #ddd;'>tsCreateDate</th>");
                sb.AppendLine("<th style='border:1px solid #ddd;'>EligibleAfter</th>");
                sb.AppendLine("</tr></thead><tbody>");

                foreach (var r in rows)
                {
                    sb.AppendLine("<tr>");
                    sb.AppendLine($"<td style='border:1px solid #ddd;'>{E(r.OpenBucket)}</td>");
                    sb.AppendLine($"<td style='border:1px solid #ddd;'>{r.TikCounter}</td>");
                    sb.AppendLine($"<td style='border:1px solid #ddd;'>{E(r.TikNumber)}</td>");
                    sb.AppendLine($"<td style='border:1px solid #ddd;'>{r.TsCreateDate:yyyy-MM-dd}</td>");
                    sb.AppendLine($"<td style='border:1px solid #ddd;'>{r.EligibleAfter:yyyy-MM-dd}</td>");
                    sb.AppendLine("</tr>");
                }

                sb.AppendLine("</tbody></table>");
                sb.AppendLine("</div>");
                return sb.ToString();
            }
        }
    }
}
