using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Odmon.Worker.Configuration;
using Odmon.Worker.Data;
using Odmon.Worker.Models;

namespace Odmon.Worker.Services
{
    /// <summary>
    /// Persists every Voicenter API request, computes weekly usage counts per endpoint type,
    /// and triggers (once-per-week) warning emails when CallHistoryDetail usage approaches the quota.
    /// </summary>
    public sealed class VoicenterUsageTracker
    {
        private readonly IntegrationDbContext _db;
        private readonly IEmailNotifier _emailNotifier;
        private readonly VoicenterCallSummarySettings _settings;
        private readonly ILogger<VoicenterUsageTracker> _logger;

        public VoicenterUsageTracker(
            IntegrationDbContext db,
            IEmailNotifier emailNotifier,
            IOptions<VoicenterCallSummarySettings> settings,
            ILogger<VoicenterUsageTracker> logger)
        {
            _db = db;
            _emailNotifier = emailNotifier;
            _settings = settings.Value;
            _logger = logger;
        }

        /// <summary>
        /// Returns the Monday 00:00:00 UTC start of the ISO-8601 week containing <paramref name="utc"/>.
        /// We use Monday (DayOfWeek == 1) as a clear, deterministic convention; configurable later if Voicenter publishes a different reset day.
        /// </summary>
        public static DateTime GetWeekStartUtc(DateTime utc)
        {
            var dayOfWeek = (int)utc.DayOfWeek;
            // Sunday == 0, Monday == 1 .. Saturday == 6. Convert to ISO: Monday = 0, Sunday = 6.
            var daysSinceMonday = (dayOfWeek + 6) % 7;
            var date = utc.Date.AddDays(-daysSinceMonday);
            return DateTime.SpecifyKind(date, DateTimeKind.Utc);
        }

        /// <summary>Persist a single request log row. Never throws — failures are logged and swallowed.</summary>
        public async Task RecordRequestAsync(
            string endpointType,
            string? callId,
            int? httpStatus,
            bool success,
            bool quotaExceeded,
            string? errorMessage,
            string? correlationId,
            CancellationToken ct)
        {
            try
            {
                var nowUtc = DateTime.UtcNow;
                var entry = new VoicenterApiRequestLog
                {
                    CreatedAtUtc = nowUtc,
                    WeekStartUtc = GetWeekStartUtc(nowUtc),
                    EndpointType = endpointType,
                    CallId = callId,
                    HttpStatus = httpStatus,
                    Success = success,
                    QuotaExceeded = quotaExceeded,
                    ErrorMessage = TrimMax(errorMessage, 2000),
                    CorrelationId = correlationId,
                };
                _db.VoicenterApiRequestLogs.Add(entry);
                await _db.SaveChangesAsync(ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "VOICENTER | Failed to persist VoicenterApiRequestLog (endpoint={Endpoint}, callId={CallId})",
                    endpointType, callId);
            }
        }

        /// <summary>Count requests for an endpoint type within the current ISO week.</summary>
        public async Task<int> GetWeeklyCountAsync(string endpointType, CancellationToken ct)
        {
            var weekStart = GetWeekStartUtc(DateTime.UtcNow);
            return await _db.VoicenterApiRequestLogs
                .AsNoTracking()
                .CountAsync(r => r.EndpointType == endpointType && r.WeekStartUtc == weekStart, ct);
        }

        /// <summary>
        /// If CallHistoryDetail usage has reached the configured threshold for the current week
        /// AND no warning has been sent for this (week, endpoint) yet, queue a warning email.
        /// Always idempotent for the week — safe to call multiple times per cycle.
        /// </summary>
        public async Task MaybeSendWeeklyWarningAsync(
            int weeklyCallHistoryDetailCount,
            int weeklyCdrListCount,
            bool quotaAlreadyExceeded,
            CancellationToken ct)
        {
            if (!_settings.UsageWarningEmailEnabled) return;
            if (weeklyCallHistoryDetailCount < _settings.WeeklyUsageWarningThreshold) return;

            var weekStart = GetWeekStartUtc(DateTime.UtcNow);
            const string endpoint = VoicenterEndpointType.CallHistoryDetail;

            var alreadySent = await _db.VoicenterQuotaWarningStates
                .AsNoTracking()
                .AnyAsync(w => w.WeekStartUtc == weekStart && w.EndpointType == endpoint, ct);
            if (alreadySent) return;

            try
            {
                _db.VoicenterQuotaWarningStates.Add(new VoicenterQuotaWarningState
                {
                    WeekStartUtc = weekStart,
                    EndpointType = endpoint,
                    WarningSentAtUtc = DateTime.UtcNow,
                    RequestCountAtWarning = weeklyCallHistoryDetailCount,
                    Threshold = _settings.WeeklyUsageWarningThreshold,
                    HardLimit = _settings.WeeklyUsageHardLimit,
                });
                await _db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException)
            {
                // Race: another process sent it concurrently. Treat as already sent.
                return;
            }

            var subject = $"Voicenter weekly usage approaching limit ({weeklyCallHistoryDetailCount}/{_settings.WeeklyUsageHardLimit})";
            var body =
                $"Endpoint: {endpoint}\n" +
                $"Week start (UTC, Monday): {weekStart:yyyy-MM-dd HH:mm:ss}\n" +
                $"Current weekly CallHistoryDetail requests: {weeklyCallHistoryDetailCount}\n" +
                $"Current weekly CdrList requests (informational, separate quota): {weeklyCdrListCount}\n" +
                $"Warning threshold: {_settings.WeeklyUsageWarningThreshold}\n" +
                $"Hard limit (Voicenter): {_settings.WeeklyUsageHardLimit}\n" +
                $"Quota already exceeded this cycle: {quotaAlreadyExceeded}\n\n" +
                $"Recommendation:\n" +
                $" - Reduce unnecessary CallHistoryDetail requests.\n" +
                $" - Consider raising the weekly limit with Voicenter (User 203570).\n" +
                $" - Verify dedup / processing-state filters are active.";

            _emailNotifier.QueueCriticalAlert(
                subject,
                body,
                source: "VoicenterUsageTracker",
                alertType: "VoicenterWeeklyUsageWarning");

            _logger.LogWarning(
                "VOICENTER | Weekly usage warning sent | Endpoint={Endpoint}, Count={Count}, Threshold={Threshold}, HardLimit={HardLimit}, WeekStart={WeekStart:yyyy-MM-dd}",
                endpoint, weeklyCallHistoryDetailCount, _settings.WeeklyUsageWarningThreshold, _settings.WeeklyUsageHardLimit, weekStart);
        }

        private static string? TrimMax(string? s, int max) =>
            string.IsNullOrEmpty(s) ? s : (s.Length <= max ? s : s[..max]);
    }
}
