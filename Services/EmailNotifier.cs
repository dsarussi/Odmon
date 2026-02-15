using System.Net;
using System.Net.Mail;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Channels;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Odmon.Worker.Services
{
    /// <summary>
    /// Email message queued for background delivery.
    /// </summary>
    public sealed class EmailMessage
    {
        public string Subject { get; init; } = string.Empty;
        public string Body { get; init; } = string.Empty;
        public bool IsHtml { get; init; }
        public string? Fingerprint { get; init; }
        public EmailMessageType Type { get; init; }
    }

    public enum EmailMessageType { Critical, DailySummary, Digest }

    /// <summary>
    /// Production email notifier with deduplication, rate limiting, and non-blocking delivery.
    /// Uses System.Net.Mail.SmtpClient for Gmail SMTP (TLS on port 587).
    ///
    /// Gmail setup: Create an App Password at https://myaccount.google.com/apppasswords
    /// and provide it via the Email__Password environment variable.
    /// </summary>
    public sealed class EmailNotifier : IEmailNotifier, IDisposable
    {
        private readonly ILogger<EmailNotifier> _logger;
        private readonly IConfiguration _config;
        private readonly Channel<EmailMessage> _queue;

        // Rate limiting: track email timestamps in a sliding window
        private readonly object _rateLock = new();
        private readonly List<DateTime> _sentTimestamps = new();

        // In-memory dedup cache (keyed by fingerprint)
        private readonly object _dedupLock = new();
        private readonly Dictionary<string, DedupEntry> _dedupCache = new();

        public EmailNotifier(ILogger<EmailNotifier> logger, IConfiguration config)
        {
            _logger = logger;
            _config = config;
            _queue = Channel.CreateBounded<EmailMessage>(new BoundedChannelOptions(100)
            {
                FullMode = BoundedChannelFullMode.DropOldest
            });
        }

        /// <summary>The channel reader for the background service to consume.</summary>
        public ChannelReader<EmailMessage> Reader => _queue.Reader;

        // ================================================================
        // IEmailNotifier implementation
        // ================================================================

        public void QueueCriticalAlert(string subject, string body, string? exceptionType = null, string? source = null)
        {
            if (!IsEnabled()) return;

            var fingerprint = ComputeFingerprint(exceptionType, body, source);

            // Check dedup
            if (IsDuplicate(fingerprint))
            {
                _logger.LogInformation(
                    "EMAIL SUPPRESSED (dedup) | Fingerprint={Fingerprint}, Subject={Subject}",
                    fingerprint[..12], subject);
                return;
            }

            // Check rate limit
            if (IsRateLimited())
            {
                _logger.LogWarning(
                    "EMAIL SUPPRESSED (rate limit) | Subject={Subject}, MaxPerHour={MaxPerHour}",
                    subject, GetMaxEmailsPerHour());
                IncrementSuppressed(fingerprint);
                return;
            }

            var msg = new EmailMessage
            {
                Subject = $"[ODMON ALERT] {subject}",
                Body = body,
                IsHtml = false,
                Fingerprint = fingerprint,
                Type = EmailMessageType.Critical
            };

            if (_queue.Writer.TryWrite(msg))
            {
                RecordSent(fingerprint);
                _logger.LogInformation(
                    "EMAIL QUEUED | Type=Critical, Subject={Subject}, Fingerprint={Fingerprint}",
                    subject, fingerprint[..12]);
            }
            else
            {
                _logger.LogWarning("EMAIL DROPPED (queue full) | Subject={Subject}", subject);
            }
        }

        public async Task SendDailySummaryAsync(string subject, string htmlBody, CancellationToken ct)
        {
            if (!IsEnabled()) return;

            var msg = new EmailMessage
            {
                Subject = $"[ODMON Daily] {subject}",
                Body = htmlBody,
                IsHtml = true,
                Type = EmailMessageType.DailySummary
            };

            await _queue.Writer.WriteAsync(msg, ct);
            _logger.LogInformation("EMAIL QUEUED | Type=DailySummary, Subject={Subject}", subject);
        }

        public async Task SendDigestAsync(string subject, string htmlBody, CancellationToken ct)
        {
            if (!IsEnabled()) return;

            var msg = new EmailMessage
            {
                Subject = $"[ODMON Digest] {subject}",
                Body = htmlBody,
                IsHtml = true,
                Type = EmailMessageType.Digest
            };

            await _queue.Writer.WriteAsync(msg, ct);
            _logger.LogInformation("EMAIL QUEUED | Type=Digest, Subject={Subject}", subject);
        }

        // ================================================================
        // SMTP sending (called by EmailBackgroundService)
        // ================================================================

        /// <summary>
        /// Sends an email via SMTP. Non-throwing: logs errors and returns false on failure.
        /// </summary>
        public async Task<bool> SendEmailAsync(EmailMessage message, CancellationToken ct)
        {
            try
            {
                var host = _config["Email:SmtpHost"] ?? "smtp.gmail.com";
                var port = _config.GetValue<int>("Email:SmtpPort", 587);
                var useTls = _config.GetValue<bool>("Email:UseTls", true);
                var username = _config["Email:Username"] ?? string.Empty;
                var password = _config["Email:Password"] ?? string.Empty;
                var recipients = _config.GetSection("Email:Recipients").Get<string[]>() ?? Array.Empty<string>();

                if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))
                {
                    _logger.LogWarning("SMTP FAILURE | Email credentials not configured. Skipping email: {Subject}", message.Subject);
                    return false;
                }

                if (recipients.Length == 0)
                {
                    _logger.LogWarning("SMTP FAILURE | No recipients configured. Skipping email: {Subject}", message.Subject);
                    return false;
                }

#pragma warning disable SYSLIB0014 // SmtpClient is obsolete but functional in .NET 8
                using var smtp = new SmtpClient(host, port)
                {
                    EnableSsl = useTls,
                    Credentials = new NetworkCredential(username, password),
                    Timeout = 30_000
                };

                using var mail = new MailMessage
                {
                    From = new MailAddress(username, "ODMON Monitor"),
                    Subject = message.Subject,
                    Body = message.Body,
                    IsBodyHtml = message.IsHtml
                };

                foreach (var r in recipients)
                {
                    if (!string.IsNullOrWhiteSpace(r))
                        mail.To.Add(r.Trim());
                }

                await smtp.SendMailAsync(mail, ct);
#pragma warning restore SYSLIB0014

                _logger.LogInformation(
                    "EMAIL SENT | Type={Type}, Subject={Subject}, Recipients={Recipients}",
                    message.Type, message.Subject, string.Join(";", recipients));

                // Record in rate limiter
                lock (_rateLock)
                {
                    _sentTimestamps.Add(DateTime.UtcNow);
                }

                return true;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex,
                    "SMTP FAILURE | Failed to send email. Subject={Subject}, Error={Error}",
                    message.Subject, ex.Message);
                return false;
            }
        }

        // ================================================================
        // Fingerprint computation (deterministic, unit-testable)
        // ================================================================

        /// <summary>
        /// Computes a SHA-256 fingerprint from exception type, message, and source.
        /// Normalized: trimmed, lowercased, line numbers stripped from stack traces.
        /// </summary>
        internal static string ComputeFingerprint(string? exceptionType, string? message, string? source)
        {
            var normalized = NormalizeForFingerprint(message);
            var input = $"{exceptionType ?? "Unknown"}|{normalized}|{source ?? "Unknown"}";
            var hash = SHA256.HashData(Encoding.UTF8.GetBytes(input));
            return Convert.ToHexString(hash).ToLowerInvariant();
        }

        /// <summary>
        /// Normalizes an error message for fingerprinting:
        /// - Trims whitespace
        /// - Lowercases
        /// - Strips numeric IDs (e.g., RunId, TikCounter values) to group similar errors
        /// - Strips line numbers from stack traces
        /// </summary>
        internal static string NormalizeForFingerprint(string? message)
        {
            if (string.IsNullOrWhiteSpace(message))
                return string.Empty;

            var s = message.Trim().ToLowerInvariant();
            // Strip common numeric identifiers that vary per occurrence
            // e.g., "RunId=abc123def456" → "RunId=<id>"
            s = System.Text.RegularExpressions.Regex.Replace(s, @"runid=[a-f0-9]+", "runid=<id>");
            // Strip line numbers in stack traces: ":line 123" → ":line <n>"
            s = System.Text.RegularExpressions.Regex.Replace(s, @":line \d+", ":line <n>");
            // Strip specific numeric values after = (e.g., TikCounter=12345)
            s = System.Text.RegularExpressions.Regex.Replace(s, @"(?<==)\d{3,}", "<n>");
            return s;
        }

        // ================================================================
        // Dedup logic
        // ================================================================

        internal bool IsDuplicate(string fingerprint)
        {
            var windowMinutes = _config.GetValue<int>("Email:DedupWindowMinutes", 60);
            var cutoff = DateTime.UtcNow.AddMinutes(-windowMinutes);

            lock (_dedupLock)
            {
                if (_dedupCache.TryGetValue(fingerprint, out var entry))
                {
                    entry.OccurrenceCount++;
                    entry.LastSeenUtc = DateTime.UtcNow;

                    if (entry.LastEmailSentUtc.HasValue && entry.LastEmailSentUtc.Value > cutoff)
                    {
                        entry.SuppressedCount++;
                        return true; // duplicate within window
                    }

                    // Window expired — allow sending again
                    return false;
                }

                // First time seeing this fingerprint — not a duplicate
                _dedupCache[fingerprint] = new DedupEntry
                {
                    FirstSeenUtc = DateTime.UtcNow,
                    LastSeenUtc = DateTime.UtcNow,
                    OccurrenceCount = 1,
                    SuppressedCount = 0,
                    LastEmailSentUtc = null
                };
                return false;
            }
        }

        internal void RecordSent(string fingerprint)
        {
            lock (_dedupLock)
            {
                if (_dedupCache.TryGetValue(fingerprint, out var entry))
                {
                    entry.LastEmailSentUtc = DateTime.UtcNow;
                    entry.SuppressedCount = 0; // reset suppressed count on send
                }
            }

            lock (_rateLock)
            {
                _sentTimestamps.Add(DateTime.UtcNow);
            }
        }

        internal void IncrementSuppressed(string fingerprint)
        {
            lock (_dedupLock)
            {
                if (_dedupCache.TryGetValue(fingerprint, out var entry))
                {
                    entry.SuppressedCount++;
                }
            }
        }

        // ================================================================
        // Rate limiting
        // ================================================================

        internal bool IsRateLimited()
        {
            var maxPerHour = GetMaxEmailsPerHour();
            if (maxPerHour <= 0) return false;

            var cutoff = DateTime.UtcNow.AddHours(-1);

            lock (_rateLock)
            {
                _sentTimestamps.RemoveAll(t => t < cutoff);
                return _sentTimestamps.Count >= maxPerHour;
            }
        }

        private int GetMaxEmailsPerHour() => _config.GetValue<int>("Email:MaxEmailsPerHour", 10);
        private bool IsEnabled() => _config.GetValue<bool>("Email:Enabled", false);

        // ================================================================
        // Digest helpers
        // ================================================================

        /// <summary>
        /// Returns a snapshot of all fingerprints with suppressed counts > 0.
        /// Used by the digest email to summarize suppressed alerts.
        /// </summary>
        public List<SuppressedAlertInfo> GetSuppressedAlerts()
        {
            lock (_dedupLock)
            {
                var result = new List<SuppressedAlertInfo>();
                foreach (var (fp, entry) in _dedupCache)
                {
                    if (entry.SuppressedCount > 0)
                    {
                        result.Add(new SuppressedAlertInfo
                        {
                            Fingerprint = fp[..Math.Min(12, fp.Length)],
                            OccurrenceCount = entry.OccurrenceCount,
                            SuppressedCount = entry.SuppressedCount,
                            FirstSeenUtc = entry.FirstSeenUtc,
                            LastSeenUtc = entry.LastSeenUtc
                        });
                    }
                }
                return result;
            }
        }

        /// <summary>Resets suppressed counts after a digest is sent.</summary>
        public void ClearSuppressedCounts()
        {
            lock (_dedupLock)
            {
                foreach (var entry in _dedupCache.Values)
                {
                    entry.SuppressedCount = 0;
                }
            }
        }

        public void Dispose()
        {
            _queue.Writer.TryComplete();
        }

        // ================================================================
        // Internal types
        // ================================================================

        internal class DedupEntry
        {
            public DateTime FirstSeenUtc { get; set; }
            public DateTime LastSeenUtc { get; set; }
            public int OccurrenceCount { get; set; }
            public int SuppressedCount { get; set; }
            public DateTime? LastEmailSentUtc { get; set; }
        }

        public class SuppressedAlertInfo
        {
            public string Fingerprint { get; set; } = string.Empty;
            public int OccurrenceCount { get; set; }
            public int SuppressedCount { get; set; }
            public DateTime FirstSeenUtc { get; set; }
            public DateTime LastSeenUtc { get; set; }
        }
    }
}
