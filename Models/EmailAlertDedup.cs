namespace Odmon.Worker.Models
{
    /// <summary>
    /// Tracks deduplicated email alert fingerprints to prevent spam.
    /// Stored in IntegrationDb for durability across worker restarts.
    /// </summary>
    public class EmailAlertDedup
    {
        public int Id { get; set; }

        /// <summary>SHA-256 hash of (ExceptionType + normalized message + source).</summary>
        public string Fingerprint { get; set; } = string.Empty;

        public string? ExceptionType { get; set; }
        public string? Source { get; set; }
        public string? Subject { get; set; }

        public DateTime FirstSeenUtc { get; set; }
        public DateTime LastSeenUtc { get; set; }

        /// <summary>Total number of times this fingerprint has been seen.</summary>
        public int OccurrenceCount { get; set; } = 1;

        /// <summary>When the last email was actually sent for this fingerprint (null = never sent).</summary>
        public DateTime? LastEmailSentUtc { get; set; }

        /// <summary>Number of occurrences suppressed (not emailed) since the last sent email.</summary>
        public int SuppressedCount { get; set; }
    }
}
