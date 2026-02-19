namespace Odmon.Worker.Services
{
    /// <summary>
    /// Email notification service for ODMON operational alerts.
    /// All methods are non-blocking — they queue emails for background delivery.
    /// If SMTP is unavailable, failures are logged but never crash the worker.
    /// </summary>
    public interface IEmailNotifier
    {
        /// <summary>
        /// Queue an immediate critical alert email (subject to dedup + rate limiting).
        /// Only use for: worker crash, circuit breaker, BoardId==0, repeated failures.
        /// </summary>
        void QueueCriticalAlert(string subject, string body, string? exceptionType = null, string? source = null);

        /// <summary>
        /// Queue a daily summary email. Typically called once per day by the background service.
        /// </summary>
        Task SendDailySummaryAsync(string subject, string htmlBody, CancellationToken ct);

        /// <summary>
        /// Queue a digest email with suppressed alert counts. Called periodically during incidents.
        /// </summary>
        Task SendDigestAsync(string subject, string htmlBody, CancellationToken ct);
    }
}
