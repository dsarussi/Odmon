namespace Odmon.Worker.Models
{
    public enum CaseIntakeDocumentType
    {
        NotificationForm,
        DemandForm
    }

    public enum CaseIntakeSourceStrength
    {
        Primary,
        Fallback
    }

    public enum CaseIntakeFieldStatus
    {
        Valid,
        Missing,
        Invalid,
        Ambiguous
    }

    public enum CaseIntakeDocumentReadStatus
    {
        Parsed,
        ExtractionFailed
    }

    public enum DemandFinancialCandidateType
    {
        VehicleDamageAmount,
        TotalDemandAmount,
        TotalPaidAmount,
        UnclassifiedTotalAmount,
        DeductibleRelatedAmount
    }

    /// <summary>Read-only source row from dbo.vwExportToOuterSystems_Documents.</summary>
    public sealed record OdcanitCaseDocument(
        long Id,
        string Name,
        string? Path,
        int TikCounter,
        string? TikVisualId,
        string? DocType,
        string? DocTypeDescription,
        DateTime? CreateDate);

    public sealed record ClassifiedCaseDocument(
        OdcanitCaseDocument Source,
        CaseIntakeDocumentType BusinessType,
        CaseIntakeSourceStrength SourceStrength);

    public sealed record CaseIntakeField<TValue>(
        TValue Value,
        CaseIntakeFieldStatus Status,
        long SourceDocumentId,
        string SourceDocumentName,
        string? SourceLabel,
        string? RawValue,
        string? ValidationMessage);

    public sealed record NotificationFormFields(
        CaseIntakeField<string?> ClaimNumber,
        CaseIntakeField<DateOnly?> EventDate,
        CaseIntakeField<string?> PolicyNumber,
        CaseIntakeField<string?> PolicyHolderName,
        CaseIntakeField<string?> PolicyHolderId,
        CaseIntakeField<string?> PolicyHolderPhone,
        CaseIntakeField<string?> DriverName,
        CaseIntakeField<string?> DriverId,
        CaseIntakeField<string?> DriverPhone,
        CaseIntakeField<string?> MainCarNumber,
        CaseIntakeField<string?> ThirdPartyCarNumber);

    public sealed record NotificationFormReadResult(
        ClassifiedCaseDocument Document,
        CaseIntakeDocumentReadStatus Status,
        NotificationFormFields? Fields,
        string? ErrorMessage);

    public sealed record DemandFinancialCandidate(
        DemandFinancialCandidateType CandidateType,
        CaseIntakeField<decimal?> Amount);

    public sealed record DemandFormFields(
        CaseIntakeField<string?> ClaimNumber,
        CaseIntakeField<DateOnly?> EventDate,
        CaseIntakeField<string?> PolicyNumber,
        CaseIntakeField<string?> PolicyHolderName,
        CaseIntakeField<string?> PolicyHolderId,
        CaseIntakeField<string?> MainCarNumber,
        CaseIntakeField<string?> ThirdPartyCarNumber,
        CaseIntakeField<decimal?> AppraiserFeeAmount,
        CaseIntakeField<decimal?> LossOfValueAmount,
        IReadOnlyList<DemandFinancialCandidate> FinancialCandidates);

    public sealed record DemandFormReadResult(
        ClassifiedCaseDocument Document,
        CaseIntakeDocumentReadStatus Status,
        DemandFormFields? Fields,
        string? ErrorMessage);

    public sealed record CaseIntakeCandidateSource(
        long DocumentId,
        string DocumentName,
        CaseIntakeDocumentType BusinessType,
        CaseIntakeSourceStrength SourceStrength,
        string? SourceLabel);

    public sealed record CaseIntakeValueCandidate<TValue>(
        TValue Value,
        IReadOnlyList<CaseIntakeCandidateSource> Sources);

    public sealed record CaseIntakeSelectedSource(
        CaseIntakeDocumentType BusinessType,
        CaseIntakeSourceStrength SourceStrength,
        IReadOnlyList<CaseIntakeCandidateSource> SupportingDocuments);

    public sealed record MergedCaseIntakeField<TValue>(
        TValue SelectedValue,
        CaseIntakeSelectedSource? SelectedSource,
        IReadOnlyList<CaseIntakeValueCandidate<TValue>> ValidCandidates,
        CaseIntakeFieldStatus ValidationStatus,
        bool CrossValidationSucceeded,
        bool HasConflict);

    public sealed record MergedCaseIntakeFields(
        MergedCaseIntakeField<string?> ClaimNumber,
        MergedCaseIntakeField<DateOnly?> EventDate,
        MergedCaseIntakeField<string?> PolicyNumber,
        MergedCaseIntakeField<string?> PolicyHolderName,
        MergedCaseIntakeField<string?> PolicyHolderId,
        MergedCaseIntakeField<string?> PolicyHolderPhone,
        MergedCaseIntakeField<string?> DriverName,
        MergedCaseIntakeField<string?> DriverId,
        MergedCaseIntakeField<string?> DriverPhone,
        MergedCaseIntakeField<string?> MainCarNumber,
        MergedCaseIntakeField<string?> ThirdPartyCarNumber,
        MergedCaseIntakeField<decimal?> AppraiserFeeAmount,
        MergedCaseIntakeField<decimal?> LossOfValueAmount);

    public sealed record CaseIntakeReadResult(
        int TikCounter,
        IReadOnlyList<ClassifiedCaseDocument> Documents,
        IReadOnlyList<NotificationFormReadResult> NotificationForms,
        IReadOnlyList<DemandFormReadResult> DemandForms,
        MergedCaseIntakeFields MergedFields);

    public static class CaseIntakeDocumentClassifier
    {
        public const string DigitalNotificationFormName = "טופס_דיווח_דיגיטלי_1";
        public const string CompanyDemandLetterName = "מכתב_דרישה_בשם_לחברה_1";
        public const string PrivatePartyDemandLetterName = "מכתב_שיבוב_לגורם_פרטי_1";

        public static bool TryClassify(
            string? sourceName,
            out CaseIntakeDocumentType businessType,
            out CaseIntakeSourceStrength sourceStrength)
        {
            switch (sourceName)
            {
                case DigitalNotificationFormName:
                    businessType = CaseIntakeDocumentType.NotificationForm;
                    sourceStrength = CaseIntakeSourceStrength.Primary;
                    return true;

                case CompanyDemandLetterName:
                case PrivatePartyDemandLetterName:
                    businessType = CaseIntakeDocumentType.DemandForm;
                    sourceStrength = CaseIntakeSourceStrength.Fallback;
                    return true;

                default:
                    businessType = default;
                    sourceStrength = default;
                    return false;
            }
        }
    }
}
