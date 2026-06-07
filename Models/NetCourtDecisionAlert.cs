namespace Odmon.Worker.Models
{
    public class NetCourtDecisionAlert
    {
        public long Id { get; set; }
        public string DocumentIdentity { get; set; } = string.Empty;
        public int TikCounter { get; set; }
        public string? TikNumber { get; set; }
        public int? ClientNumber { get; set; }
        public long NetCourtCounter { get; set; }
        public long? ODDocID { get; set; }
        public long? CourtDocumentID { get; set; }
        public long? DecisionID { get; set; }
        public int DocType { get; set; }
        public string? Description { get; set; }
        public string? DecisionDesc { get; set; }
        public DateTime? DocDate { get; set; }
        public DateTime? tsCreateDate { get; set; }
        public string? IntendedRecipientEmail { get; set; }
        public string? ActualRecipientEmail { get; set; }
        public string EmailMode { get; set; } = string.Empty;
        public string Status { get; set; } = string.Empty;
        public DateTime CreatedAtUtc { get; set; }
        public DateTime? AlertQueuedAtUtc { get; set; }
        public string? ErrorMessage { get; set; }
    }

    public static class NetCourtDecisionAlertStatuses
    {
        public const string Pending = "Pending";
        public const string Baseline = "Baseline";
        public const string TestEmailQueued = "TestEmailQueued";
        public const string LiveEmailQueued = "LiveEmailQueued";
        public const string MissingRouting = "MissingRouting";
        public const string ResolutionFailed = "ResolutionFailed";
        public const string QueueFailed = "QueueFailed";

        public static bool IsTerminal(string? status)
            => status is Baseline or TestEmailQueued or LiveEmailQueued or MissingRouting;
    }
}
