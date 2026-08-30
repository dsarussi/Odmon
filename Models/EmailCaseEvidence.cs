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
        IReadOnlyList<EmailEvidenceValue> DriverPhones);
}
