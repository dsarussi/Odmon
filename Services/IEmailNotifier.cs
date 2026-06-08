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
        /// When alertType is provided, subject becomes "ODMON ALERT — {alertType}"; otherwise "[ODMON ALERT] {subject}".
        /// Optional environmentName and serverName are prepended to the body when provided.
        /// </summary>
        void QueueCriticalAlert(string subject, string body, string? exceptionType = null, string? source = null, string? alertType = null, string? environmentName = null, string? serverName = null);

        /// <summary>
        /// Queues a normal email to explicit recipients. Global critical-alert recipients are not used.
        /// Returns false when email is disabled, rate limited, recipients are empty, or the queue is full.
        /// </summary>
        bool QueueEmail(
            string subject,
            string body,
            IReadOnlyCollection<string> recipients,
            bool isHtml = false,
            IReadOnlyCollection<EmailAttachmentDescriptor>? attachments = null);

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
