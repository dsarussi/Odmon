namespace Odmon.Worker.Models
{
    public sealed class EmailFilingMailboxState
    {
        public long Id { get; set; }
        public string Mailbox { get; set; } = string.Empty;
        public string FolderId { get; set; } = string.Empty;
        public string? DeltaLink { get; set; }
        public DateTime ProcessingFromUtc { get; set; }
        public DateTime? LastSuccessfulSyncUtc { get; set; }
        public DateTime CreatedAtUtc { get; set; }
        public DateTime UpdatedAtUtc { get; set; }
    }

    public sealed class EmailFilingDiagnostic
    {
        public long Id { get; set; }
        public string Mailbox { get; set; } = string.Empty;
        public string MessageFingerprint { get; set; } = string.Empty;
        public DateTime? ReceivedDateTimeUtc { get; set; }
        public int TikCandidateCount { get; set; }
        public int ResolvedTikCount { get; set; }
        public int SuspectNotCaseCount { get; set; }
        public int CourtCandidateCount { get; set; }
        public int ResolvedCourtCount { get; set; }
        public int TargetCount { get; set; }
        public int DedupHitCount { get; set; }
        public string ObserverClassifications { get; set; } = string.Empty;
        public string FinalDecision { get; set; } = string.Empty;
        public DateTime CreatedAtUtc { get; set; }
        public List<EmailFilingCandidateDiagnostic> Candidates { get; set; } = [];
        public List<EmailFilingTargetDiagnostic> Targets { get; set; } = [];
    }

    public sealed class EmailFilingCandidateDiagnostic
    {
        public long Id { get; set; }
        public long EmailFilingDiagnosticId { get; set; }
        public EmailFilingDiagnostic EmailFilingDiagnostic { get; set; } = null!;
        public string CandidateType { get; set; } = string.Empty;
        public string Source { get; set; } = string.Empty;
        public string Candidate { get; set; } = string.Empty;
        public string ResolutionStatus { get; set; } = string.Empty;
        public int? ResolvedTikCounter { get; set; }
        public string? ResolvedTikNumber { get; set; }
    }

    public sealed class EmailFilingTargetDiagnostic
    {
        public long Id { get; set; }
        public long EmailFilingDiagnosticId { get; set; }
        public EmailFilingDiagnostic EmailFilingDiagnostic { get; set; } = null!;
        public int TikCounter { get; set; }
        public string TikNumber { get; set; } = string.Empty;
        public bool RealWriteAllowlisted { get; set; }
        public string DedupResult { get; set; } = string.Empty;
        public string Decision { get; set; } = string.Empty;
    }

    /// <summary>
    /// Durable per-email/per-case write reservation and success proof. The keyed
    /// fingerprint remains the only persisted email identity; no Graph ID,
    /// Message-ID, subject, body, or recipient data is stored here.
    /// </summary>
    public sealed class EmailFilingDedup
    {
        public long Id { get; set; }
        public string MessageFingerprint { get; set; } = string.Empty;
        public int TikCounter { get; set; }
        public string TikNumber { get; set; } = string.Empty;
        public string Status { get; set; } = EmailFilingWriteStates.Succeeded;
        public int? OdcanitDocCounter { get; set; }
        public long ExpectedFileLength { get; set; }
        public string? LastErrorCategory { get; set; }
        public DateTime CreatedAtUtc { get; set; }
        public DateTime UpdatedAtUtc { get; set; }
        public DateTime? FiledAtUtc { get; set; }
        public byte[] RowVersion { get; set; } = [];
    }

    public static class EmailFilingWriteStates
    {
        public const string Reserved = "RESERVED";
        public const string CreatingDocument = "CREATING_DOCUMENT";
        public const string DocumentCreated = "DOCUMENT_CREATED";
        public const string Copying = "COPYING";
        public const string CopyFailed = "COPY_FAILED";
        public const string CreateUncertain = "CREATE_UNCERTAIN";
        public const string Succeeded = "SUCCEEDED";
    }

    public static class EmailFilingConstants
    {
        public const string TikCandidateType = "TIK";
        public const string CourtCandidateType = "COURT";
        public const string SubjectSource = "Subject";
        public const string BodySource = "Body";

        public const string Resolved = "RESOLVED";
        public const string SuspectNotCase = "SUSPECT_NOT_CASE";
        public const string CourtNotFound = "COURT_NOT_FOUND";
        public const string CourtAmbiguous = "COURT_AMBIGUOUS";

        public const string NotPreviouslyFiled = "NOT_PREVIOUSLY_FILED";
        public const string Duplicate = "DUPLICATE";
        public const string RecoveryPending = "RECOVERY_PENDING";

        public const string DryRunWouldFile = "DRY_RUN_WOULD_FILE";
        public const string SkipDuplicate = "SKIP_DUPLICATE";
        public const string RealWriteDisabled = "REAL_WRITE_DISABLED";
        public const string NotAllowlisted = "NOT_ALLOWLISTED";
        public const string ReadyToFile = "READY_TO_FILE";
        public const string Filed = "FILED";
        public const string MimeFailed = "MIME_FAILED";
        public const string WriteFailed = "WRITE_FAILED";
        public const string ManualRepairRequired = "MANUAL_REPAIR_REQUIRED";
        public const string NoTikCandidates = "NO_TIK_CANDIDATES";
        public const string NoValidTik = "NO_VALID_TIK";
        public const string AllTargetsDuplicate = "ALL_TARGETS_DUPLICATE";
    }
}
