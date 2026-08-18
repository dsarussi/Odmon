using System.Text.RegularExpressions;
using Odmon.Worker.Models;

namespace Odmon.Worker.Services
{
    public sealed partial class DemandFormParser
    {
        private static readonly string[] ExplicitClaimNumberLabels =
            ["מספר תביעה", "מספר:תביעה", "מס' תביעה", "מס׳ תביעה"];
        private static readonly string[] OurClaimNumberLabels =
            ["תביעתנו מספר", "תביעתנו מס'", "תביעתנו מס׳", "תביעתנו", "תביעת:נו"];
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
            ["דמי שמאות", "שמאותדמי", "שכר טרחת שמאי", "שכ\"ט שמאי", "שכ״ט שמאי"];
        private static readonly string[] ExactLossOfValueLabels = ["ירידת ערך הרכב"];
        private static readonly string[] FallbackLossOfValueLabels = ["ירידת ערך"];
        private static readonly string[] VehicleDamageLabels =
            ["נזק לרכב עפ\"י דו\"ח שמאי", "נזק לרכב עפ״י דו״ח שמאי", "נזק לרכב על פי דו\"ח שמאי",
             "נזק לרכב על פי דו״ח שמאי", "נזק לרכב", "לרכבנזק"];
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
            var ourClaimNumber = PreferContext(
                ExtractContextValues(
                    ImmediateDemandClaimNumberRegex(),
                    searchableText,
                    "תביעתנו / תביעת:נו"),
                TextExtractor.Extract(lines, OurClaimNumberLabels));
            var policyHolderNameContext = CombineExtractions(
                ExtractContextValues(
                    PolicyHolderNameRegex(),
                    searchableText,
                    "לפקודת מבוטחנו"),
                ExtractContextValues(
                    PolicyHolderNameBeforeAnchorRegex(),
                    searchableText,
                    "לפקודת מבוטחנו"),
                ExtractContextValues(
                    CompanyPolicyHolderNameRegex(),
                    searchableText,
                    "ת.ז ... שם בעל הפוליסה"));
            var companyPolicyHolderId = ExtractContextValues(
                CompanyPolicyHolderIdRegex(),
                searchableText,
                "שם בעל הפוליסה ... מספר:רישוי");
            var mainVehicleContext = CombineExtractions(
                ExtractVehicleAfterAnchor(
                    searchableText,
                    "הרכב המבוטח בחברתנו",
                    "הרכב שבבעלותך",
                    "הרכב המבוטח בחברתנו"),
                ExtractContextValues(
                    CompanyInsuredVehicleRegex(),
                    searchableText,
                    "שם בעל הפוליסה ... מספר:רישוי"));
            var thirdPartyVehicleContext = CombineExtractions(
                ExtractVehicleAfterAnchor(
                    searchableText,
                    "הרכב שבבעלותך",
                    null,
                    "הרכב שבבעלותך"),
                document.Name == CaseIntakeDocumentClassifier.CompanyDemandLetterName
                    ? ExtractContextValues(
                        CompanyHeadingVehicleRegex(),
                        searchableText,
                        "הנדון")
                    : CreateExtraction(Array.Empty<RawFieldMatch>()));
            return new DemandFormFields(
                ClaimNumber: CaseIntakeClaimNumberResolver.Resolve(
                    document,
                    TextExtractor.Extract(lines, ExplicitClaimNumberLabels),
                    ourClaimNumber),
                EventDate: CaseIntakeFieldFactory.Build(
                    PreferContext(
                        ExtractAccidentDates(lines),
                        TextExtractor.Extract(lines, EventDateLabels)),
                    document,
                    CaseIntakeFieldValidators.ValidateDate),
                PolicyNumber: CaseIntakeFieldFactory.Build(
                    TextExtractor.Extract(lines, PolicyNumberLabels),
                    document,
                    raw => CaseIntakeFieldValidators.ValidateNumber(raw, "Policy number")),
                PolicyHolderName: CaseIntakeFieldFactory.Build(
                    PreferContext(
                        policyHolderNameContext,
                        TextExtractor.Extract(lines, PolicyHolderNameLabels)),
                    document,
                    CaseIntakeFieldValidators.ValidateName),
                PolicyHolderId: CaseIntakeFieldFactory.Build(
                    PreferContext(
                        companyPolicyHolderId,
                        TextExtractor.Extract(lines, PolicyHolderIdLabels)),
                    document,
                    CaseIntakeFieldValidators.ValidateIdentifierNumber),
                MainCarNumber: CaseIntakeFieldFactory.Build(
                    PreferContext(
                        mainVehicleContext,
                        TextExtractor.Extract(lines, MainCarNumberLabels)),
                    document,
                    CaseIntakeFieldValidators.ValidateVehicleNumber),
                ThirdPartyCarNumber: CaseIntakeFieldFactory.Build(
                    PreferContext(
                        thirdPartyVehicleContext,
                        TextExtractor.Extract(lines, ThirdPartyCarNumberLabels)),
                    document,
                    CaseIntakeFieldValidators.ValidateVehicleNumber),
                AppraiserFeeAmount: CaseIntakeFieldFactory.Build(
                    ExtractValidatedMonetaryField(lines, AppraiserFeeLabels),
                    document,
                    CaseIntakeFieldValidators.ValidateAmount),
                LossOfValueAmount: CaseIntakeFieldFactory.Build(
                    ExtractLossOfValue(lines, searchableText),
                    document,
                    CaseIntakeFieldValidators.ValidateAmount),
                FinancialCandidates: ExtractFinancialCandidates(lines, document));
        }

        private static RawFieldExtraction ExtractValidatedMonetaryField(
            IReadOnlyList<string> lines,
            IReadOnlyList<string> labels)
        {
            var matches = TextExtractor.ExtractAll(lines, labels)
                .Select(match => new
                {
                    Match = match,
                    Validation = CaseIntakeFieldValidators.ValidateAmount(match.Value)
                })
                .Where(item =>
                    item.Validation.Status == CaseIntakeFieldStatus.Valid &&
                    item.Validation.Value.HasValue)
                .GroupBy(item => item.Validation.Value!.Value)
                .Select(group => group.First().Match)
                .ToArray();

            return CreateExtraction(matches);
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
                ? TextExtractor.Extract(
                    lines.Where(line =>
                            !line.Contains("השתתפות עצמית", StringComparison.Ordinal) &&
                            !line.Contains("לירידת ערך", StringComparison.Ordinal))
                        .ToArray(),
                    FallbackLossOfValueLabels)
                : exact;
        }

        private static RawFieldExtraction ExtractAccidentDates(
            IReadOnlyList<string> lines)
        {
            var matches = new List<RawFieldMatch>();
            for (var lineIndex = 0; lineIndex < lines.Count; lineIndex++)
            {
                var window = lineIndex + 1 < lines.Count
                    ? $"{lines[lineIndex]}\n{lines[lineIndex + 1]}"
                    : lines[lineIndex];
                var accidentIndex = window.IndexOf("תאונת דרכים", StringComparison.Ordinal);
                var fromDateIndex = window.IndexOf("מיום", StringComparison.Ordinal);
                if (accidentIndex < 0 || fromDateIndex < 0)
                {
                    continue;
                }

                var printDateIndex = window.IndexOf("תאריך הדפסה", StringComparison.Ordinal);
                var candidate = DateTokenRegex().Matches(window)
                    .Select(match => new
                    {
                        Match = match,
                        Validation = CaseIntakeFieldValidators.ValidateDate(match.Value)
                    })
                    .Where(item =>
                        item.Validation.Status == CaseIntakeFieldStatus.Valid &&
                        (printDateIndex < 0 ||
                         Math.Abs(item.Match.Index - fromDateIndex) <
                         Math.Abs(item.Match.Index - printDateIndex)))
                    .OrderBy(item => Math.Abs(item.Match.Index - fromDateIndex))
                    .FirstOrDefault();
                if (candidate != null)
                {
                    matches.Add(new RawFieldMatch("תאונת דרכים ... מיום", candidate.Match.Value));
                }
            }

            return CreateExtraction(
                matches
                    .GroupBy(match => match.Value, StringComparer.Ordinal)
                    .Select(group => group.First())
                    .ToArray());
        }

        private static RawFieldExtraction ExtractVehicleAfterAnchor(
            string text,
            string anchor,
            string? stopAnchor,
            string sourceLabel)
        {
            const int maximumWindowLength = 240;
            var matches = new List<RawFieldMatch>();
            var searchStart = 0;
            while (searchStart < text.Length)
            {
                var anchorIndex = text.IndexOf(anchor, searchStart, StringComparison.Ordinal);
                if (anchorIndex < 0)
                {
                    break;
                }

                var windowStart = anchorIndex + anchor.Length;
                var windowEnd = Math.Min(text.Length, windowStart + maximumWindowLength);
                if (stopAnchor != null)
                {
                    var stopIndex = text.IndexOf(stopAnchor, windowStart, StringComparison.Ordinal);
                    if (stopIndex >= 0 && stopIndex < windowEnd)
                    {
                        windowEnd = stopIndex;
                    }
                }

                var vehicleMatch = VehicleNumberRegex().Matches(
                        text[windowStart..windowEnd])
                    .Select(match => new
                    {
                        Match = match,
                        Validation = CaseIntakeFieldValidators.ValidateVehicleNumber(match.Value)
                    })
                    .FirstOrDefault(item =>
                        item.Validation.Status == CaseIntakeFieldStatus.Valid);
                if (vehicleMatch != null)
                {
                    matches.Add(new RawFieldMatch(sourceLabel, vehicleMatch.Match.Value));
                }

                searchStart = anchorIndex + anchor.Length;
            }

            return CreateExtraction(
                matches
                    .GroupBy(match => match.Value, StringComparer.Ordinal)
                    .Select(group => group.First())
                    .ToArray());
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

        private static RawFieldExtraction PreferContext(
            RawFieldExtraction contextual,
            RawFieldExtraction fallback)
            => contextual.Status == RawFieldExtractionStatus.Missing
                ? fallback
                : contextual;

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
            @"(?:תביעת:נו|תביעתנו)\s*:?\s*(?<value>\p{Nd}+(?:[-/._]\p{Nd}+)*)(?![\p{L}\p{Nd}_./-])",
            RegexOptions.CultureInvariant)]
        private static partial Regex ImmediateDemandClaimNumberRegex();

        [GeneratedRegex(
            @"(?<!\d)(?:\d{1,2}[./-]\d{1,2}[./-]\d{4}|\d{4}-\d{1,2}-\d{1,2})(?!\d)",
            RegexOptions.CultureInvariant)]
        private static partial Regex DateTokenRegex();

        [GeneratedRegex(
            @"לפקודת\s+מבוטחנו\s+(?<value>[\u0590-\u05FF][\u0590-\u05FF'׳״""-]*(?:\s+[\u0590-\u05FF][\u0590-\u05FF'׳״""-]*){1,3})(?=\s*[.,;:\r\n]|$)",
            RegexOptions.CultureInvariant)]
        private static partial Regex PolicyHolderNameRegex();

        [GeneratedRegex(
            @"(?:^|[:.])\s*(?<value>[\u0590-\u05FF][\u0590-\u05FF'׳״""-]*(?:\s+[\u0590-\u05FF][\u0590-\u05FF'׳״""-]*){1,3}?)יש\s+להעביר(?=[^\r\n]{0,200}לפקודת\s+מבוטחנו)",
            RegexOptions.CultureInvariant | RegexOptions.Singleline)]
        private static partial Regex PolicyHolderNameBeforeAnchorRegex();

        [GeneratedRegex(
            @"ת\.ז(?<value>[\u0590-\u05FF][\u0590-\u05FF'׳״""-]*(?:\s+[\u0590-\u05FF][\u0590-\u05FF'׳״""-]*){1,3})שם\s+בעל\s+הפוליסה\s*:",
            RegexOptions.CultureInvariant)]
        private static partial Regex CompanyPolicyHolderNameRegex();

        [GeneratedRegex(
            @"שם\s+בעל\s+הפוליסה\s*:\s*(?<value>\p{Nd}{5,9})\s+מספר\s*:\s*רישוי",
            RegexOptions.CultureInvariant)]
        private static partial Regex CompanyPolicyHolderIdRegex();

        [GeneratedRegex(
            @"שם\s+בעל\s+הפוליסה\s*:\s*\p{Nd}{5,9}\s+מספר\s*:\s*רישוי\s*(?:\r?\n)+\s*(?<value>\p{Nd}(?:[ .-]?\p{Nd}){6,7})(?!\p{Nd})",
            RegexOptions.CultureInvariant)]
        private static partial Regex CompanyInsuredVehicleRegex();

        [GeneratedRegex(
            @"(?:^|\n)[^\r\n]*הנדון[^\r\n]{0,200}?(?<value>\p{Nd}(?:[ .-]?\p{Nd}){6,7})(?!\p{Nd})",
            RegexOptions.CultureInvariant)]
        private static partial Regex CompanyHeadingVehicleRegex();

        [GeneratedRegex(
            @"(?<!\p{Nd})\p{Nd}(?:[ .-]?\p{Nd}){6,7}(?!\p{Nd})",
            RegexOptions.CultureInvariant)]
        private static partial Regex VehicleNumberRegex();

        [GeneratedRegex(
            @"(?:^|[^\p{L}])(?:הרכב[ \t]*)?ירידת[ \t]+ערך(?:[ \t]*הרכב)?[ \t]*:?[ \t]*(?<value>[-+]?\p{Nd}[\p{Nd},.]*[-+]?[ \t]*₪?)",
            RegexOptions.CultureInvariant | RegexOptions.Multiline)]
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
