using System;

namespace Odmon.Worker.Models
{
    public class HearingNearestSnapshot
    {
        public int Id { get; set; }
        public int TikCounter { get; set; }
        public long BoardId { get; set; }
        public long MondayItemId { get; set; }
        /// <summary>The source event currently observed for this mapped case.</summary>
        public int? ObservedSourceEventId { get; set; }
        public DateTime? NearestStartDateUtc { get; set; }
        public int? NearestMeetStatus { get; set; }
        /// <summary>The source event whose cancellation/transfer was successfully processed.</summary>
        public int? DeliveredStatusSourceEventId { get; set; }
        /// <summary>The successfully processed MeetStatus (1 or 2) for that source event.</summary>
        public int? DeliveredMeetStatus { get; set; }
        /// <summary>
        /// Source event for which a delivery attempt was durably prepared but has
        /// not yet been proven successful.
        /// </summary>
        public int? PendingStatusSourceEventId { get; set; }
        /// <summary>The cancellation/transfer status associated with the pending attempt.</summary>
        public int? PendingMeetStatus { get; set; }
        /// <summary>When the pending attempt was first durably recorded.</summary>
        public DateTime? PendingStatusSinceUtc { get; set; }
        /// <summary>
        /// Whether Monday already had the desired label before the pending attempt.
        /// True means an exact label after a crash cannot prove the mutation occurred.
        /// </summary>
        public bool? PendingInitialStatusWasDesired { get; set; }
        public string? JudgeName { get; set; }
        public string? City { get; set; }
        public DateTime LastSyncedAtUtc { get; set; }
    }
}
