namespace Odmon.Worker.Models
{
    public enum EmailEvidenceType
    {
        InternalTikNumber,
        ClaimNumber,
        CourtCaseNumber,
        VehicleNumber,
        EventDate,
        ClientHint,
        InsuredName,
        DriverPhone
    }

    public enum EmailEvidenceSource
    {
        Subject,
        Body
    }

    public enum EmailEvidenceExtractionKind
    {
        ExplicitLabel,
        StructuredPattern,
        Contextual
    }

    public enum EmailSourceTemplate
    {
        Generic,
        DirectInsurance
    }

    /// <summary>
    /// Transient evidence extracted from an email. Values in this model can
    /// contain personal data and must not be persisted merely for diagnostics.
    /// </summary>
    public sealed record EmailEvidenceValue(
        EmailEvidenceType EvidenceType,
        string NormalizedValue,
        EmailEvidenceSource Source,
        EmailEvidenceExtractionKind ExtractionKind);

    /// <summary>
    /// Canonical, in-memory email evidence. Collections intentionally retain
    /// source provenance and allow more than one value of each evidence type.
    /// </summary>
    public sealed record EmailCaseEvidence(
        IReadOnlyList<EmailEvidenceValue> InternalTikNumbers,
        IReadOnlyList<EmailEvidenceValue> ClaimNumbers,
        IReadOnlyList<EmailEvidenceValue> CourtCaseNumbers,
        IReadOnlyList<EmailEvidenceValue> VehicleNumbers,
        IReadOnlyList<EmailEvidenceValue> EventDates,
        IReadOnlyList<EmailEvidenceValue> ClientHints,
        IReadOnlyList<EmailEvidenceValue> InsuredNames,
        IReadOnlyList<EmailEvidenceValue> DriverPhones)
    {
        /// <summary>
        /// Classification used by the Direct Insurance authority route. A
        /// recognized Direct source still requires the specifically labelled
        /// preferred claim field before this can grant Direct authority.
        /// </summary>
        public EmailSourceTemplate SourceTemplate { get; init; } = EmailSourceTemplate.Generic;

        /// <summary>
        /// Privacy-safe source classification for diagnostics. It may use
        /// strong sender/forwarded-source indicators but grants no authority by
        /// itself.
        /// </summary>
        public EmailSourceTemplate DetectedSourceTemplate { get; init; } = EmailSourceTemplate.Generic;

        /// <summary>
        /// Source-specific primary claim evidence. It remains transient and is
        /// populated only when a conservatively recognized template defines a
        /// stronger claim-number label.
        /// </summary>
        public IReadOnlyList<EmailEvidenceValue> PreferredClaimNumbers { get; init; } = [];
    }
}
