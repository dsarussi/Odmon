using Odmon.Worker.Models;

namespace Odmon.Worker.Services
{
    public sealed class DemandFormParser
    {
        private static readonly string[] ExplicitClaimNumberLabels =
            ["מספר תביעה", "מספר:תביעה", "מס' תביעה", "מס׳ תביעה"];
        private static readonly string[] OurClaimNumberLabels =
            ["תביעתנו מספר", "תביעתנו מס'", "תביעתנו מס׳", "תביעתנו"];
        private static readonly string[] EventDateLabels =
            ["תאריך אירוע", "תאונת דרכים מיום"];
        private static readonly string[] PolicyNumberLabels =
            ["מספר פוליסה", "מס' פוליסה", "מס׳ פוליסה"];
        private static readonly string[] PolicyHolderNameLabels =
            ["שם בעל הפוליסה", "שם בעל פוליסה"];
        private static readonly string[] PolicyHolderIdLabels =
            ["תעודת זהות בעל הפוליסה", "תעודת זהות בעל פוליסה", "ת.ז. בעל הפוליסה",
             "ת.ז בעל הפוליסה", "ת.ז. בעל פוליסה", "ת.ז בעל פוליסה", "ת.ז.", "ת.ז"];
        private static readonly string[] MainCarNumberLabels =
            ["מספר רישוי רכב המבוטח", "מספר רישוי רכב מבוטח", "מספר רישוי המבוטח",
             "רכב המבוטח מס'", "רכב המבוטח מס׳", "רכב מרשתנו מס'", "רכב מרשתנו מס׳",
             "רכבנו מס'", "רכבנו מס׳", "מספר רישוי"];
        private static readonly string[] ThirdPartyCarNumberLabels =
            ["מספר רישוי רכב צד ג'", "מספר רישוי רכב צד ג׳", "מספר רישוי רכב הפוגע",
             "מספר רישוי רכבכם", "רכב צד ג' מס'", "רכב צד ג׳ מס׳", "רכב הפוגע מס'",
             "רכב הפוגע מס׳", "רכבכם מס'", "רכבכם מס׳"];
        private static readonly string[] AppraiserFeeLabels =
            ["דמי שמאות", "שכר טרחת שמאי", "שכ\"ט שמאי", "שכ״ט שמאי"];
        private static readonly string[] LossOfValueLabels = ["ירידת ערך"];
        private static readonly string[] VehicleDamageLabels =
            ["נזק לרכב עפ\"י דו\"ח שמאי", "נזק לרכב עפ״י דו״ח שמאי", "נזק לרכב על פי דו\"ח שמאי",
             "נזק לרכב על פי דו״ח שמאי", "נזק לרכב"];
        private static readonly string[] TotalDemandLabels =
            ["סה\"כ סכום הדרישה", "סה״כ סכום הדרישה", "סה\"כ דרישה", "סה״כ דרישה",
             "סך הכל דרישה", "סה\"כ לתשלום", "סה״כ לתשלום"];
        private static readonly string[] TotalPaidLabels =
            ["סה\"כ ששולם", "סה״כ ששולם", "סה\"כ שולם", "סה״כ שולם", "סך ששולם"];
        private static readonly string[] UnclassifiedTotalLabels = ["סה\"כ", "סה״כ", "סך הכל"];
        private static readonly string[] DeductibleLabels =
            ["ניכוי השתתפות עצמית", "השתתפות עצמית"];

        private static readonly string[] AllLabels =
            ExplicitClaimNumberLabels
                .Concat(OurClaimNumberLabels)
                .Concat(EventDateLabels)
                .Concat(PolicyNumberLabels)
                .Concat(PolicyHolderNameLabels)
                .Concat(PolicyHolderIdLabels)
                .Concat(MainCarNumberLabels)
                .Concat(ThirdPartyCarNumberLabels)
                .Concat(AppraiserFeeLabels)
                .Concat(LossOfValueLabels)
                .Concat(VehicleDamageLabels)
                .Concat(TotalDemandLabels)
                .Concat(TotalPaidLabels)
                .Concat(UnclassifiedTotalLabels)
                .Concat(DeductibleLabels)
                .Distinct(StringComparer.Ordinal)
                .OrderByDescending(label => label.Length)
                .ToArray();

        private static readonly CaseIntakeTextFieldExtractor TextExtractor = new(AllLabels);

        public DemandFormFields Parse(string extractedText, OdcanitCaseDocument document)
        {
            ArgumentNullException.ThrowIfNull(extractedText);
            ArgumentNullException.ThrowIfNull(document);

            var lines = TextExtractor.NormalizeLines(extractedText);
            return new DemandFormFields(
                ClaimNumber: CaseIntakeClaimNumberResolver.Resolve(
                    document,
                    TextExtractor.Extract(lines, ExplicitClaimNumberLabels),
                    TextExtractor.Extract(lines, OurClaimNumberLabels)),
                EventDate: CaseIntakeFieldFactory.Build(
                    TextExtractor.Extract(lines, EventDateLabels),
                    document,
                    CaseIntakeFieldValidators.ValidateDate),
                PolicyNumber: CaseIntakeFieldFactory.Build(
                    TextExtractor.Extract(lines, PolicyNumberLabels),
                    document,
                    raw => CaseIntakeFieldValidators.ValidateNumber(raw, "Policy number")),
                PolicyHolderName: CaseIntakeFieldFactory.Build(
                    TextExtractor.Extract(lines, PolicyHolderNameLabels),
                    document,
                    CaseIntakeFieldValidators.ValidateName),
                PolicyHolderId: CaseIntakeFieldFactory.Build(
                    TextExtractor.Extract(lines, PolicyHolderIdLabels),
                    document,
                    CaseIntakeFieldValidators.ValidateIdentifierNumber),
                MainCarNumber: CaseIntakeFieldFactory.Build(
                    TextExtractor.Extract(lines, MainCarNumberLabels),
                    document,
                    CaseIntakeFieldValidators.ValidateVehicleNumber),
                ThirdPartyCarNumber: CaseIntakeFieldFactory.Build(
                    TextExtractor.Extract(lines, ThirdPartyCarNumberLabels),
                    document,
                    CaseIntakeFieldValidators.ValidateVehicleNumber),
                AppraiserFeeAmount: CaseIntakeFieldFactory.Build(
                    TextExtractor.Extract(lines, AppraiserFeeLabels),
                    document,
                    CaseIntakeFieldValidators.ValidateAmount),
                LossOfValueAmount: CaseIntakeFieldFactory.Build(
                    TextExtractor.Extract(lines, LossOfValueLabels),
                    document,
                    CaseIntakeFieldValidators.ValidateAmount),
                FinancialCandidates: ExtractFinancialCandidates(lines, document));
        }

        private static IReadOnlyList<DemandFinancialCandidate> ExtractFinancialCandidates(
            IReadOnlyList<string> lines,
            OdcanitCaseDocument document)
        {
            var candidates = new List<DemandFinancialCandidate>();
            AddCandidates(
                candidates,
                DemandFinancialCandidateType.VehicleDamageAmount,
                lines,
                VehicleDamageLabels,
                document);
            AddCandidates(
                candidates,
                DemandFinancialCandidateType.TotalDemandAmount,
                lines,
                TotalDemandLabels,
                document);
            AddCandidates(
                candidates,
                DemandFinancialCandidateType.TotalPaidAmount,
                lines,
                TotalPaidLabels,
                document);
            AddCandidates(
                candidates,
                DemandFinancialCandidateType.UnclassifiedTotalAmount,
                lines,
                UnclassifiedTotalLabels,
                document);
            AddCandidates(
                candidates,
                DemandFinancialCandidateType.DeductibleRelatedAmount,
                lines,
                DeductibleLabels,
                document);
            return candidates;
        }

        private static void AddCandidates(
            ICollection<DemandFinancialCandidate> destination,
            DemandFinancialCandidateType candidateType,
            IReadOnlyList<string> lines,
            IReadOnlyList<string> labels,
            OdcanitCaseDocument document)
        {
            foreach (var match in TextExtractor.ExtractAll(lines, labels)
                         .DistinctBy(match => (match.Label, match.Value)))
            {
                var extraction = new RawFieldExtraction(
                    RawFieldExtractionStatus.Found,
                    match.Label,
                    match.Value,
                    [match]);
                destination.Add(new DemandFinancialCandidate(
                    candidateType,
                    CaseIntakeFieldFactory.Build(
                        extraction,
                        document,
                        CaseIntakeFieldValidators.ValidateAmount)));
            }
        }
    }
}
