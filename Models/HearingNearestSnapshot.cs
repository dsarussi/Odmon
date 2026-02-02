using System;

namespace Odmon.Worker.Models
{
    public class HearingNearestSnapshot
    {
        public int Id { get; set; }
        public int TikCounter { get; set; }
        public long BoardId { get; set; }
        public long MondayItemId { get; set; }
        public DateTime? NearestStartDateUtc { get; set; }
        public int? NearestMeetStatus { get; set; }
        public string? JudgeName { get; set; }
        public string? City { get; set; }
        public DateTime LastSyncedAtUtc { get; set; }
    }
}
