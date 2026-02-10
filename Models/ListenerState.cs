using System;

namespace Odmon.Worker.Models
{
    /// <summary>
    /// Singleton row (Id=1) that persists the listener start watermark (T0).
    /// Cases with tsCreateDate >= StartedAtUtc are eligible for creation;
    /// everything before T0 is ignored unless already mapped.
    /// </summary>
    public class ListenerState
    {
        public int Id { get; set; }

        /// <summary>
        /// The UTC timestamp when the listener first started. Cases created
        /// in Odcanit before this time will never be auto-created in Monday.
        /// Set once on first run, never reset.
        /// </summary>
        public DateTime StartedAtUtc { get; set; }

        /// <summary>
        /// The last change-feed watermark used (optional, for diagnostics).
        /// </summary>
        public DateTime? LastChangeFeedWatermarkUtc { get; set; }

        /// <summary>
        /// Last time this row was updated.
        /// </summary>
        public DateTime UpdatedAtUtc { get; set; }
    }
}
