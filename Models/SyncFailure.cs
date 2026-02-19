namespace Odmon.Worker.Models
{
    /// <summary>
    /// Tracks per-case sync failures for later inspection and reprocessing.
    /// Acts as a dead-letter log: each row represents one failed attempt to sync a case to Monday.
    /// </summary>
    public class SyncFailure
    {
        public int Id { get; set; }
        public string RunId { get; set; } = string.Empty;
        public int TikCounter { get; set; }
        public string? TikNumber { get; set; }
        public long BoardId { get; set; }

        /// <summary>
        /// The operation that failed: "create", "update", "process_case".
        /// </summary>
        public string Operation { get; set; } = string.Empty;

        /// <summary>
        /// Short exception type name (e.g., "MondayApiException", "SqlException").
        /// </summary>
        public string ErrorType { get; set; } = string.Empty;

        /// <summary>
        /// The error message (truncated to 2000 chars).
        /// </summary>
        public string ErrorMessage { get; set; } = string.Empty;

        /// <summary>
        /// Truncated stack trace for diagnostics.
        /// </summary>
        public string? StackTrace { get; set; }

        public DateTime OccurredAtUtc { get; set; }

        /// <summary>
        /// Number of retry attempts made before giving up.
        /// </summary>
        public int RetryAttempts { get; set; }

        /// <summary>
        /// Set to true when the case has been successfully reprocessed.
        /// </summary>
        public bool Resolved { get; set; }

        public DateTime? ResolvedAtUtc { get; set; }
    }
}
