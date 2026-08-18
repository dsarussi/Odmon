using System.Text.RegularExpressions;
using Odmon.Worker.Models;

namespace Odmon.Worker.Services
{
    public sealed partial class DemandFormParser
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
             "רכבנו מס'", "רכבנו מס׳"];
        private static readonly string[] ThirdPartyCarNumberLabels =
            ["מספר רישוי רכב צד ג'", "מספר רישוי רכב צד ג׳", "מספר רישוי רכב הפוגע",
             "מספר רישוי רכבכם", "רכב צד ג' מס'", "רכב צד ג׳ מס׳", "רכב הפוגע מס'",
             "רכב הפוגע מס׳", "רכבכם מס'", "רכבכם מס׳"];
        private static readonly string[] AppraiserFeeLabels =
            ["דמי שמאות", "שכר טרחת שמאי", "שכ\"ט שמאי", "שכ״ט שמאי"];
        private static readonly string[] ExactLossOfValueLabels = ["ירידת ערך הרכב"];
        private static readonly string[] FallbackLossOfValueLabels = ["ירידת ערך"];
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
                .Concat(ExactLossOfValueLabels)
                .Concat(FallbackLossOfValueLabels)
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
            var searchableText = string.Join('\n', lines);
            return new DemandFormFields(
                ClaimNumber: CaseIntakeClaimNumberResolver.Resolve(
                    document,
                    TextExtractor.Extract(lines, ExplicitClaimNumberLabels),
                    TextExtractor.Extract(lines, OurClaimNumberLabels)),
                EventDate: CaseIntakeFieldFactory.Build(
                    CombineExtractions(
                        TextExtractor.Extract(lines, EventDateLabels),
                        ExtractContextValues(
                            AccidentDateRegex(),
                            searchableText,
                            "תאונת דרכים מיום")),
                    document,
                    CaseIntakeFieldValidators.ValidateDate),
                PolicyNumber: CaseIntakeFieldFactory.Build(
                    TextExtractor.Extract(lines, PolicyNumberLabels),
                    document,
                    raw => CaseIntakeFieldValidators.ValidateNumber(raw, "Policy number")),
                PolicyHolderName: CaseIntakeFieldFactory.Build(
                    CombineExtractions(
                        TextExtractor.Extract(lines, PolicyHolderNameLabels),
                        ExtractContextValues(
                            PolicyHolderNameRegex(),
                            searchableText,
                            "לפקודת מבוטחנו")),
                    document,
                    CaseIntakeFieldValidators.ValidateName),
                PolicyHolderId: CaseIntakeFieldFactory.Build(
                    TextExtractor.Extract(lines, PolicyHolderIdLabels),
                    document,
                    CaseIntakeFieldValidators.ValidateIdentifierNumber),
                MainCarNumber: CaseIntakeFieldFactory.Build(
                    CombineExtractions(
                        TextExtractor.Extract(lines, MainCarNumberLabels),
                        ExtractContextValues(
                            InsuredVehicleRegex(),
                            searchableText,
                            "הרכב המבוטח בחברתנו ... מספר רישוי")),
                    document,
                    CaseIntakeFieldValidators.ValidateVehicleNumber),
                ThirdPartyCarNumber: CaseIntakeFieldFactory.Build(
                    CombineExtractions(
                        TextExtractor.Extract(lines, ThirdPartyCarNumberLabels),
                        ExtractContextValues(
                            OtherVehicleRegex(),
                            searchableText,
                            "הרכב שבבעלותך ... מספר רישוי")),
                    document,
                    CaseIntakeFieldValidators.ValidateVehicleNumber),
                AppraiserFeeAmount: CaseIntakeFieldFactory.Build(
                    TextExtractor.Extract(lines, AppraiserFeeLabels),
                    document,
                    CaseIntakeFieldValidators.ValidateAmount),
                LossOfValueAmount: CaseIntakeFieldFactory.Build(
                    ExtractLossOfValue(lines, searchableText),
                    document,
                    CaseIntakeFieldValidators.ValidateAmount),
                FinancialCandidates: ExtractFinancialCandidates(lines, document));
        }

        private static RawFieldExtraction ExtractLossOfValue(
            IReadOnlyList<string> lines,
            string searchableText)
        {
            var exact = CombineExtractions(
                TextExtractor.Extract(lines, ExactLossOfValueLabels),
                ExtractContextValues(
                    LossOfValueAmountRegex(),
                    searchableText,
                    "ירידת ערך הרכב"));
            return exact.Status == RawFieldExtractionStatus.Missing
                ? TextExtractor.Extract(lines, FallbackLossOfValueLabels)
                : exact;
        }

        private static RawFieldExtraction ExtractContextValues(
            Regex regex,
            string text,
            string sourceLabel)
        {
            var matches = regex.Matches(text)
                .Select(match => match.Groups["value"].Value.Trim())
                .Where(value => value.Length > 0)
                .Distinct(StringComparer.Ordinal)
                .Select(value => new RawFieldMatch(sourceLabel, value))
                .ToArray();

            return CreateExtraction(matches);
        }

        private static RawFieldExtraction CombineExtractions(
            params RawFieldExtraction[] extractions)
        {
            var matches = extractions
                .SelectMany(extraction => extraction.Matches)
                .GroupBy(match => match.Value, StringComparer.Ordinal)
                .Select(group => group.First())
                .ToArray();
            return CreateExtraction(matches);
        }

        private static RawFieldExtraction CreateExtraction(
            IReadOnlyList<RawFieldMatch> matches)
            => matches.Count switch
            {
                0 => new(
                    RawFieldExtractionStatus.Missing,
                    null,
                    null,
                    Array.Empty<RawFieldMatch>()),
                1 => new(
                    RawFieldExtractionStatus.Found,
                    matches[0].Label,
                    matches[0].Value,
                    matches),
                _ => new(
                    RawFieldExtractionStatus.Ambiguous,
                    string.Join(", ", matches.Select(match => match.Label).Distinct(StringComparer.Ordinal)),
                    string.Join(" | ", matches.Select(match => match.Value)),
                    matches)
            };

        [GeneratedRegex(
            @"(?:הנדון\s*:\s*)?תאונת\s+דרכים\s+מיום\s*:?\s*(?<value>\d{1,2}[./-]\d{1,2}[./-]\d{4})(?!\d)",
            RegexOptions.CultureInvariant)]
        private static partial Regex AccidentDateRegex();

        [GeneratedRegex(
            @"לפקודת\s+מבוטחנו\s+(?<value>[\u0590-\u05FF][\u0590-\u05FF'׳״""-]*(?:\s+[\u0590-\u05FF][\u0590-\u05FF'׳״""-]*){1,3})(?=\s*[.,;:\r\n]|$)",
            RegexOptions.CultureInvariant)]
        private static partial Regex PolicyHolderNameRegex();

        [GeneratedRegex(
            @"הרכב\s+המבוטח\s+בחברתנו(?:(?!הרכב\s+(?:המבוטח\s+בחברתנו|שבבעלותך)).){0,160}?מספר\s+רישוי\s*:?\s*(?<value>\p{Nd}(?:[ .-]?\p{Nd}){6,7})(?!\p{Nd})",
            RegexOptions.CultureInvariant | RegexOptions.Singleline)]
        private static partial Regex InsuredVehicleRegex();

        [GeneratedRegex(
            @"הרכב\s+שבבעלותך(?:(?!הרכב\s+(?:המבוטח\s+בחברתנו|שבבעלותך)).){0,160}?מספר\s+רישוי\s*:?\s*(?<value>\p{Nd}(?:[ .-]?\p{Nd}){6,7})(?!\p{Nd})",
            RegexOptions.CultureInvariant | RegexOptions.Singleline)]
        private static partial Regex OtherVehicleRegex();

        [GeneratedRegex(
            @"ירידת\s+ערך\s+הרכב\s*:?\s*(?<value>[-+]?\p{Nd}[\p{Nd},.]*[-+]?\s*₪?)",
            RegexOptions.CultureInvariant)]
        private static partial Regex LossOfValueAmountRegex();

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
