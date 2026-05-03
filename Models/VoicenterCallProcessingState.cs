namespace Odmon.Worker.Models
{
    /// <summary>
    /// Per-CallID processing state. Lets the worker skip calls already known
    /// as Written / NoAI / NoMatch / Duplicate without burning Call History API quota.
    /// </summary>
    public class VoicenterCallProcessingState
    {
        public string CallId { get; set; } = string.Empty;
        public DateTime FirstSeenUtc { get; set; }
        public DateTime LastSeenUtc { get; set; }
        public DateTime LastCheckedUtc { get; set; }
        public string Status { get; set; } = VoicenterCallProcessingStatus.New;
        public int Attempts { get; set; }
        public int? TikCounter { get; set; }
        public string? TikVisualId { get; set; }
        public string? LastError { get; set; }
    }

    /// <summary>String constants for VoicenterCallProcessingState.Status.</summary>
    public static class VoicenterCallProcessingStatus
    {
        public const string New = "New";
        public const string Written = "Written";
        public const string NoAi = "NoAI";
        public const string NoMatch = "NoMatch";
        public const string Duplicate = "Duplicate";
        public const string Failed = "Failed";
        public const string QuotaExceeded = "QuotaExceeded";
    }

    /// <summary>String constants for VoicenterApiRequestLog.EndpointType.</summary>
    public static class VoicenterEndpointType
    {
        public const string CdrList = "CdrList";
        public const string CallHistoryDetail = "CallHistoryDetail";
        public const string Other = "Other";
    }
}
