namespace Odmon.Worker.Models
{
    /// <summary>
    /// Lightweight per-run metrics persisted to IntegrationDb.
    /// Used by the daily summary email to aggregate operational stats.
    /// </summary>
    public class SyncRunMetric
    {
        public int Id { get; set; }
        public string RunId { get; set; } = string.Empty;
        public DateTime StartedAtUtc { get; set; }
        public DateTime? CompletedAtUtc { get; set; }
        public int DurationMs { get; set; }

        // Bootstrap phase
        public int BootstrapCreated { get; set; }
        public int CoolingFilteredOut { get; set; }
        public int BootstrapFailed { get; set; }

        // Reconcile phase
        public int Updated { get; set; }
        public int SkippedNoChange { get; set; }
        public int SkippedInactive { get; set; }
        public int SkippedDuplicate { get; set; }
        public int Failed { get; set; }

        public bool CircuitBreakerTripped { get; set; }
        public string? DataSource { get; set; }
    }
}
