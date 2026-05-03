namespace Odmon.Worker.Voicenter
{
    public sealed class VoicenterCdrEntry
    {
        public string? CallID { get; set; }
        public string? CallerNumber { get; set; }
        public string? TargetNumber { get; set; }
        public string? Date { get; set; }
        public int Duration { get; set; }
        public string? Type { get; set; }
        public string? DialStatus { get; set; }
        public string? RecordURL { get; set; }
    }

    public sealed class VoicenterCallDetail
    {
        public string CallId { get; set; } = string.Empty;
        public string? UniqueId { get; set; }
        public DateTime? CallTime { get; set; }
        public int DurationSeconds { get; set; }
        public string? DialStatus { get; set; }
        public string? ClientPhone { get; set; }
        public string? TargetNo { get; set; }
        public string? CallerNo { get; set; }
        public string? AiSummary { get; set; }
    }

    public sealed class CasePhoneMatch
    {
        public int TikCounter { get; set; }
        public string TikNumber { get; set; } = string.Empty;
        public string MatchedField { get; set; } = string.Empty;
    }

    public sealed class VoicenterRunResult
    {
        // Core counts
        public int Fetched { get; set; }
        public int DetailsFetched { get; set; }
        public int SkippedNoAi { get; set; }
        public int SkippedNoMatch { get; set; }
        public int SkippedDuplicate { get; set; }
        public int Written { get; set; }
        public int Failed { get; set; }
        public List<string> FailedCallIds { get; set; } = [];

        // Per-cycle API request counters
        public int CdrListRequestsThisRun { get; set; }
        public int CallHistoryDetailRequestsThisRun { get; set; }

        // Weekly snapshots
        public int WeeklyCallHistoryDetailRequests { get; set; }
        public int WeeklyCdrListRequests { get; set; }
        public int WeeklyCallHistoryDetailLimit { get; set; }
        public int WeeklyCallHistoryDetailWarningThreshold { get; set; }

        // Quota / failure conditions
        public bool ApiLimitExceeded { get; set; }
        public int DetailFetchFailed { get; set; }

        // Pre-detail skip reasons (explain why a fetched CDR did not become a detail call)
        public int SkippedMissingCallId { get; set; }
        public int SkippedDuplicateBeforeDetail { get; set; }
        public int SkippedAlreadyProcessed { get; set; }
        public int SkippedDueToQuotaExceeded { get; set; }
        public int SkippedPreDetailOther { get; set; }

        // Backfill metadata
        public bool BackfillMode { get; set; }
        public DateTime? BackfillFromUtc { get; set; }
        public DateTime? BackfillToUtc { get; set; }
        public bool BackfillDryRun { get; set; }
    }
}
