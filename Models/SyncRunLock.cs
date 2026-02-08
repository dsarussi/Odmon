namespace Odmon.Worker.Models
{
    /// <summary>
    /// DB-based run lock to prevent overlapping sync runs.
    /// Only one row should exist (Id=1). The row is claimed at run start
    /// and released at run end. If a run crashes, the lock auto-expires
    /// based on ExpiresAtUtc.
    /// </summary>
    public class SyncRunLock
    {
        public int Id { get; set; }

        /// <summary>
        /// The RunId that currently holds the lock.
        /// </summary>
        public string? LockedByRunId { get; set; }

        /// <summary>
        /// When the lock was acquired.
        /// </summary>
        public DateTime? LockedAtUtc { get; set; }

        /// <summary>
        /// When the lock automatically expires (safety net for crashes).
        /// </summary>
        public DateTime? ExpiresAtUtc { get; set; }
    }
}
