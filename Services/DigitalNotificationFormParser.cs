using System.Text.RegularExpressions;
using Odmon.Worker.Models;

namespace Odmon.Worker.Services
{
    public sealed partial class DigitalNotificationFormParser
    {
        private static readonly string[] ExplicitClaimNumberLabels =
            ["מספר תביעה", "מס' תביעה", "מס׳ תביעה"];
        private static readonly string[] ReportNumberLabels =
            ["מס' דיווח", "מס׳ דיווח", "מספר דיווח"];
        private static readonly string[] EventDateLabels = ["תאריך אירוע"];
        private static readonly string[] PolicyNumberLabels = ["מספר פוליסה", "מס' פוליסה", "מס׳ פוליסה"];
        private static readonly string[] PolicyHolderNameLabels = ["שם בעל הפוליסה", "שם בעל פוליסה"];
        private static readonly string[] PolicyHolderIdLabels =
            ["תעודת זהות בעל הפוליסה", "תעודת זהות בעל פוליסה", "ת.ז. בעל פוליסה", "ת.ז בעל פוליסה"];
        private static readonly string[] PolicyHolderPhoneLabels =
            ["טלפון בעל הפוליסה", "טלפון בעל פוליסה", "סלולרי בעל הפוליסה", "סלולרי בעל פוליסה"];
        private static readonly string[] DriverNameLabels = ["שם הנהג", "שם נהג"];
        private static readonly string[] DriverIdLabels =
            ["תעודת זהות הנהג", "תעודת זהות נהג", "ת.ז. נהג", "ת.ז נהג"];
        private static readonly string[] DriverPhoneLabels =
            ["טלפון הנהג", "טלפון נהג", "סלולרי הנהג", "סלולרי נהג"];
        private static readonly string[] MainCarNumberLabels =
            ["מספר רכב מבוטח", "מספר רכב ראשי", "מספר רישוי", "מספר רכב"];
        private static readonly string[] ThirdPartyCarNumberLabels =
            ["מספר רישוי רכב צד ג'", "מספר רישוי רכב ג'", "מספר רישוי צד ג'", "מספר רכב צד ג'",
             "מספר רישוי רכב צד ג׳", "מספר רישוי רכב ג׳", "מספר רישוי צד ג׳", "מספר רכב צד ג׳"];

        private static readonly string[] AllLabels =
            ExplicitClaimNumberLabels
                .Concat(ReportNumberLabels)
                .Concat(EventDateLabels)
                .Concat(PolicyNumberLabels)
                .Concat(PolicyHolderNameLabels)
                .Concat(PolicyHolderIdLabels)
                .Concat(PolicyHolderPhoneLabels)
                .Concat(DriverNameLabels)
                .Concat(DriverIdLabels)
                .Concat(DriverPhoneLabels)
                .Concat(MainCarNumberLabels)
                .Concat(ThirdPartyCarNumberLabels)
                .Distinct(StringComparer.Ordinal)
                .OrderByDescending(label => label.Length)
                .ToArray();
        private static readonly CaseIntakeTextFieldExtractor TextExtractor = new(AllLabels);

        public NotificationFormFields Parse(string extractedText, OdcanitCaseDocument document)
        {
            ArgumentNullException.ThrowIfNull(extractedText);
            ArgumentNullException.ThrowIfNull(document);

            var lines = TextExtractor.NormalizeLines(extractedText);
            var searchableText = string.Join('\n', lines);
            var claimNumber = CaseIntakeClaimNumberResolver.Resolve(
                document,
                TextExtractor.Extract(lines, ExplicitClaimNumberLabels),
                TextExtractor.Extract(lines, ReportNumberLabels));
            var insuredDriverName = ExtractInsuredDriverName(lines);

            return new NotificationFormFields(
                ClaimNumber: claimNumber,
                EventDate: CaseIntakeFieldFactory.Build(
                    PreferContext(
                        ExtractContextValues(
                            EventDateContextRegex(),
                            searchableText,
                            "תאריך האירוע"),
                        TextExtractor.Extract(lines, EventDateLabels)),
                    document,
                    CaseIntakeFieldValidators.ValidateDate),
                PolicyNumber: CaseIntakeFieldFactory.Build(
                    TextExtractor.Extract(lines, PolicyNumberLabels),
                    document,
                    raw => CaseIntakeFieldValidators.ValidateNumber(raw, "Policy number")),
                PolicyHolderName: CaseIntakeFieldFactory.Build(
                    PreferContext(
                        ExtractContextValues(
                            PolicyHolderNameContextRegex(),
                            searchableText,
                            "שם ... ז\\דרכון ... כתובת"),
                        TextExtractor.Extract(lines, PolicyHolderNameLabels)),
                    document,
                    CaseIntakeFieldValidators.ValidateName),
                PolicyHolderId: CaseIntakeFieldFactory.Build(
                    PreferContext(
                        ExtractContextValues(
                            PolicyHolderIdContextRegex(),
                            searchableText,
                            "שם ... ז\\דרכון ... כתובת"),
                        TextExtractor.Extract(lines, PolicyHolderIdLabels)),
                    document,
                    CaseIntakeFieldValidators.ValidateIdentifierNumber),
                PolicyHolderPhone: CaseIntakeFieldFactory.Build(
                    PreferContext(
                        ExtractContextValues(
                            PolicyHolderPhoneContextRegex(),
                            searchableText,
                            "טל:בבית:נייד ... כתובת מייל"),
                        TextExtractor.Extract(lines, PolicyHolderPhoneLabels)),
                    document,
                    CaseIntakeFieldValidators.ValidatePhone),
                DriverName: CaseIntakeFieldFactory.Build(
                    insuredDriverName,
                    document,
                    CaseIntakeFieldValidators.ValidateName),
                DriverId: CaseIntakeFieldFactory.Build(
                    PreferContext(
                        ExtractContextValues(
                            InsuredDriverIdContextRegex(),
                            searchableText,
                            "שם:ת.ז\\דרכון ... נייד"),
                        TextExtractor.Extract(lines, DriverIdLabels)),
                    document,
                    CaseIntakeFieldValidators.ValidateIdentifierNumber),
                DriverPhone: CaseIntakeFieldFactory.Build(
                    PreferContext(
                        ExtractContextValues(
                            InsuredDriverPhoneContextRegex(),
                            searchableText,
                            "שם:ת.ז\\דרכון ... נייד"),
                        TextExtractor.Extract(lines, DriverPhoneLabels)),
                    document,
                    CaseIntakeFieldValidators.ValidatePhone),
                MainCarNumber: CaseIntakeFieldFactory.Build(
                    PreferContext(
                        ExtractContextValues(
                            InsuredVehicleContextRegex(),
                            searchableText,
                            "מס' רישוי ... יצרן ... שנת ייצור"),
                        TextExtractor.Extract(lines, MainCarNumberLabels)),
                    document,
                    CaseIntakeFieldValidators.ValidateVehicleNumber),
                ThirdPartyCarNumber: CaseIntakeFieldFactory.Build(
                    PreferContext(
                        ExtractContextValues(
                            ThirdPartyVehicleContextRegex(),
                            searchableText,
                            "סוג הרכב ... מס' רישוי"),
                        TextExtractor.Extract(lines, ThirdPartyCarNumberLabels)),
                    document,
                    CaseIntakeFieldValidators.ValidateVehicleNumber));
        }

        private static RawFieldExtraction ExtractInsuredDriverName(
            IReadOnlyList<string> lines)
        {
            var sectionStart = FindLineIndex(lines, 0, "פרטי הנהג");
            if (sectionStart < 0)
            {
                sectionStart = 0;
            }

            var sectionEnd = FindFirstThirdPartyLine(lines, sectionStart);
            var sectionLines = lines
                .Skip(sectionStart)
                .Take(sectionEnd - sectionStart)
                .ToArray();
            var sectionText = string.Join('\n', sectionLines);
            var contextual = ExtractContextValues(
                InsuredDriverNameContextRegex(),
                sectionText,
                "פרטי הנהג ... שם:ת.ז\\דרכון ... נייד");
            if (contextual.Status != RawFieldExtractionStatus.Missing)
            {
                return contextual;
            }

            return TextExtractor.Extract(
                sectionLines,
                DriverNameLabels);
        }

        private static int FindFirstThirdPartyLine(
            IReadOnlyList<string> lines,
            int searchStart)
        {
            for (var index = searchStart; index < lines.Count; index++)
            {
                if (lines[index].Contains("סוג הרכב", StringComparison.Ordinal) ||
                    lines[index].Contains("פרטי צד ג", StringComparison.Ordinal) ||
                    lines[index].Contains("פרטי הצד השלישי", StringComparison.Ordinal))
                {
                    return index;
                }
            }

            return lines.Count;
        }

        private static int FindLineIndex(
            IReadOnlyList<string> lines,
            int searchStart,
            string value)
        {
            for (var index = searchStart; index < lines.Count; index++)
            {
                if (lines[index].Contains(value, StringComparison.Ordinal))
                {
                    return index;
                }
            }

            return -1;
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

            return matches.Length switch
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
                    sourceLabel,
                    string.Join(" | ", matches.Select(match => match.Value)),
                    matches)
            };
        }

        private static RawFieldExtraction PreferContext(
            RawFieldExtraction contextual,
            RawFieldExtraction fallback)
            => contextual.Status == RawFieldExtractionStatus.Missing
                ? fallback
                : contextual;

        [GeneratedRegex(
            @"תאריך\s+האירוע\s+(?<value>\d{1,2}[./-]\d{1,2}[./-]\d{4})(?=\s*:שעת\s+האירוע)",
            RegexOptions.CultureInvariant)]
        private static partial Regex EventDateContextRegex();

        [GeneratedRegex(
            @"ת""(?<value>[\u0590-\u05FF][\u0590-\u05FF'׳״""-]*(?:\s+[\u0590-\u05FF][\u0590-\u05FF'׳״""-]*){1,3})שם\s*:(?=ז\\דרכון[^\r\n]*כתובת)",
            RegexOptions.CultureInvariant)]
        private static partial Regex PolicyHolderNameContextRegex();

        [GeneratedRegex(
            @"שם\s*:ז\\דרכון\s+(?<value>\p{Nd}{5,9})(?=:[^\r\n]*כתובת)",
            RegexOptions.CultureInvariant)]
        private static partial Regex PolicyHolderIdContextRegex();

        [GeneratedRegex(
            @"טל\s*:בבית\s*:נייד\s+(?<value>\+?\p{Nd}[\p{Nd} .()\-]{7,})(?=:כתובת\s+מייל)",
            RegexOptions.CultureInvariant)]
        private static partial Regex PolicyHolderPhoneContextRegex();

        [GeneratedRegex(
            @"(?:^|\n|פרטי\s+הנהג)\s*:?\s*(?<value>[\u0590-\u05FF][\u0590-\u05FF'׳״""-]*(?:\s+[\u0590-\u05FF][\u0590-\u05FF'׳״""-]*){1,3})(?=שם\s*:\s*ת\.ז\s*[\\/]\s*דרכון[^\r\n]*נייד)",
            RegexOptions.CultureInvariant)]
        private static partial Regex InsuredDriverNameContextRegex();

        [GeneratedRegex(
            @"שם\s*:ת\.ז\\דרכון\s+(?<value>\p{Nd}{5,9})(?=:[^\r\n]*נייד)",
            RegexOptions.CultureInvariant)]
        private static partial Regex InsuredDriverIdContextRegex();

        [GeneratedRegex(
            @"שם\s*:ת\.ז\\דרכון\s+\p{Nd}{5,9}\s*:נייד\s+(?<value>\+?\p{Nd}[\p{Nd} .()\-]{7,})(?=$|\r|\n)",
            RegexOptions.CultureInvariant)]
        private static partial Regex InsuredDriverPhoneContextRegex();

        [GeneratedRegex(
            @"מס(?:'|׳)\s+רישוי\s+(?<value>\p{Nd}(?:[ .-]?\p{Nd}){6,7})(?=\s*:[^\r\n]*(?:יצרן|שנת\s+ייצור))",
            RegexOptions.CultureInvariant)]
        private static partial Regex InsuredVehicleContextRegex();

        [GeneratedRegex(
            @"סוג\s+הרכב\s+(?<value>\p{Nd}(?:[ .-]?\p{Nd}){6,7})\s*:[^\r\n]*מס(?:'|׳)\s+רישוי",
            RegexOptions.CultureInvariant)]
        private static partial Regex ThirdPartyVehicleContextRegex();
    }
}
