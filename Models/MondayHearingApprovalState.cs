using System;

namespace Odmon.Worker.Models
{
    /// <summary>
    /// Phase-2: tracks Monday hearing approval status per item to support idempotent Odcanit write-back.
    /// </summary>
    public class MondayHearingApprovalState
    {
        public int Id { get; set; }

        public long BoardId { get; set; }
        public long MondayItemId { get; set; }
        public int TikCounter { get; set; }

        public string? LastKnownStatus { get; set; }
        public string? FirstDecision { get; set; }

        public DateTime? LastWriteAtUtc { get; set; }
        public DateTime UpdatedAtUtc { get; set; }
    }
}

