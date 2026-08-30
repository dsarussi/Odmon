using System.Text.RegularExpressions;
using Odmon.Worker.Models;

namespace Odmon.Worker.Services
{
    public interface IEmailCaseEvidenceExtractor
    {
        EmailCaseEvidence Extract(
            string? subject,
            string? normalizedBody,
            int maximumCandidates);
    }

    /// <summary>
    /// Pure extraction and normalization only. This service performs no case
    /// resolution and grants no filing authority.
    /// </summary>
    public sealed class EmailCaseEvidenceExtractor : IEmailCaseEvidenceExtractor
    {
        private const int MaximumIdentifierLength = 64;
        private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(1);

        // Capture the complete slash/dot token. This prevents 94/1 from being
        // extracted out of the verified legacy VisualId 94/1.7076.
        private static readonly Regex InternalTikRegex = CreateRegex(
            @"(?<![0-9/])(?<value>[0-9]+/[0-9]+(?:\.[0-9]+)*)(?![0-9/]|\.[0-9])");

        private static readonly Regex ClaimRegex = CreateRegex(
            @"(?:(?:מספר|מס\s*['׳’])\s*תביעה|תיק\s*תביעה\s*(?:מספר|מס\s*['׳’])|זיהוי\s*נוסף)\s*[.:：#\-–—]?\s*(?<value>[\p{L}\p{N}][\p{L}\p{N}._/\-]{0,63})",
            RegexOptions.IgnoreCase);

        private static readonly Regex CourtRegex = CreateRegex(
            @"(?:(?:מספר|מס\s*['׳’])\s*הליך(?:\s*בית\s*משפט)?|(?:מספר|מס\s*['׳’])\s*תיק\s*בית\s*משפט|תיק\s*בית\s*משפט\s*(?:מספר|מס\s*['׳’]))\s*[.:：#\-–—]?\s*(?<value>[\p{L}\p{N}][\p{L}\p{N}._/\-]{0,63})",
            RegexOptions.IgnoreCase);

        // Israeli court case number: sequence (1-6 digits), month, two-digit
        // year. Boundaries prevent extraction from longer numeric/hyphen tokens
        // while allowing natural Hebrew punctuation such as "ל-8069-09-24".
        private static readonly Regex StructuredCourtRegex = CreateRegex(
            @"(?<![0-9]-)(?<![\p{L}\p{N}])(?<value>[0-9]{1,6}-(?:0[1-9]|1[0-2])-[0-9]{2})(?![\p{L}\p{N}-])");

        private static readonly Regex VehicleRegex = CreateRegex(
            @"(?:(?:מספר|מס\s*['׳’])\s*רכב|(?:מספר|מס\s*['׳’])\s*רישוי\.?)\s*[.:：#\-–—]?\s*(?<value>[0-9][0-9\- \t]{3,16}[0-9])",
            RegexOptions.IgnoreCase);

        private static readonly Regex EventDateRegex = CreateRegex(
            @"(?:תאריך\s*אירוע|תאריך\s*האירוע)\s*[.:：#\-–—]?\s*(?<value>[0-9]{1,4}[./\-][0-9]{1,2}[./\-][0-9]{1,4})",
            RegexOptions.IgnoreCase);

        private static readonly Regex InsuredNameRegex = CreateRegex(
            @"(?:שם\s*מבוטחנו|שם\s*מבוטח|שם\s*בעל\s*פוליסה|insured\s*name|policy[ \t\-]*holder(?:\s*name)?)\s*[.:：#\-–—]?\s*(?<value>[^\r\n;,|]{1,128})",
            RegexOptions.IgnoreCase);

        private static readonly Regex DriverPhoneRegex = CreateRegex(
            @"(?:מספר\s*טלפון\s*נהג|טלפון\s*נהג|סלולרי\s*נהג|נייד\s*נהג|driver\s*:?\s*phone)\s*[.:：#\-–—]?\s*(?<value>\+?[0-9()\- \t]{8,24})",
            RegexOptions.IgnoreCase);

        public EmailCaseEvidence Extract(
            string? subject,
            string? normalizedBody,
            int maximumCandidates)
        {
            if (maximumCandidates <= 0)
                throw new ArgumentOutOfRangeException(nameof(maximumCandidates));

            var values = new List<EmailEvidenceValue>();
            ExtractFromSource(subject, EmailEvidenceSource.Subject, values, maximumCandidates);
            ExtractFromSource(normalizedBody, EmailEvidenceSource.Body, values, maximumCandidates);

            var distinct = values
                .DistinctBy(value => new
                {
                    value.EvidenceType,
                    value.NormalizedValue,
                    value.Source,
                    value.ExtractionKind
                })
                .ToArray();

            return new EmailCaseEvidence(
                OfType(distinct, EmailEvidenceType.InternalTikNumber),
                OfType(distinct, EmailEvidenceType.ClaimNumber),
                OfType(distinct, EmailEvidenceType.CourtCaseNumber),
                OfType(distinct, EmailEvidenceType.VehicleNumber),
                OfType(distinct, EmailEvidenceType.EventDate),
                [], // Client extraction is intentionally deferred until its text contract is verified.
                OfType(distinct, EmailEvidenceType.InsuredName),
                OfType(distinct, EmailEvidenceType.DriverPhone));
        }

        private static void ExtractFromSource(
            string? input,
            EmailEvidenceSource source,
            ICollection<EmailEvidenceValue> destination,
            int maximumCandidates)
        {
            if (string.IsNullOrWhiteSpace(input))
                return;

            AddMatches(
                InternalTikRegex,
                input,
                EmailEvidenceType.InternalTikNumber,
                source,
                EmailEvidenceExtractionKind.StructuredPattern,
                static value => NormalizeIdentifier(value),
                destination,
                maximumCandidates);
            AddMatches(
                ClaimRegex,
                input,
                EmailEvidenceType.ClaimNumber,
                source,
                EmailEvidenceExtractionKind.ExplicitLabel,
                static value => NormalizeIdentifier(value),
                destination,
                maximumCandidates);
            AddMatches(
                CourtRegex,
                input,
                EmailEvidenceType.CourtCaseNumber,
                source,
                EmailEvidenceExtractionKind.ExplicitLabel,
                static value => NormalizeIdentifier(value),
                destination,
                maximumCandidates);
            AddMatches(
                StructuredCourtRegex,
                input,
                EmailEvidenceType.CourtCaseNumber,
                source,
                EmailEvidenceExtractionKind.StructuredPattern,
                NormalizeStructuredCourtCaseNumber,
                destination,
                maximumCandidates,
                skipExistingValue: true);
            AddMatches(
                VehicleRegex,
                input,
                EmailEvidenceType.VehicleNumber,
                source,
                EmailEvidenceExtractionKind.ExplicitLabel,
                EmailEvidenceNormalization.VehicleNumber,
                destination,
                maximumCandidates);
            AddMatches(
                EventDateRegex,
                input,
                EmailEvidenceType.EventDate,
                source,
                EmailEvidenceExtractionKind.ExplicitLabel,
                EmailEvidenceNormalization.EventDate,
                destination,
                maximumCandidates);
            AddMatches(
                InsuredNameRegex,
                input,
                EmailEvidenceType.InsuredName,
                source,
                EmailEvidenceExtractionKind.ExplicitLabel,
                static value => EmailEvidenceNormalization.Text(value),
                destination,
                maximumCandidates);
            AddMatches(
                DriverPhoneRegex,
                input,
                EmailEvidenceType.DriverPhone,
                source,
                EmailEvidenceExtractionKind.ExplicitLabel,
                EmailEvidenceNormalization.Phone,
                destination,
                maximumCandidates);
        }

        private static void AddMatches(
            Regex regex,
            string input,
            EmailEvidenceType evidenceType,
            EmailEvidenceSource source,
            EmailEvidenceExtractionKind extractionKind,
            Func<string, string?> normalize,
            ICollection<EmailEvidenceValue> destination,
            int maximumCandidates,
            bool skipExistingValue = false)
        {
            foreach (Match match in regex.Matches(input))
            {
                var normalized = normalize(match.Groups["value"].Value);
                if (string.IsNullOrWhiteSpace(normalized))
                    continue;

                if (skipExistingValue && destination.Any(existing =>
                        existing.EvidenceType == evidenceType &&
                        existing.Source == source &&
                        string.Equals(
                            existing.NormalizedValue,
                            normalized,
                            StringComparison.Ordinal)))
                {
                    continue;
                }

                if (destination.Count >= maximumCandidates)
                {
                    throw new InvalidDataException(
                        "Email canonical evidence exceeds the configured candidate-count limit.");
                }

                destination.Add(new EmailEvidenceValue(
                    evidenceType,
                    normalized,
                    source,
                    extractionKind));
            }
        }

        private static string? NormalizeIdentifier(string value)
        {
            var normalized = value.Trim().TrimEnd('.', ',', ';', ':');
            return normalized.Length is > 0 and <= MaximumIdentifierLength
                ? normalized
                : null;
        }

        private static string? NormalizeStructuredCourtCaseNumber(string value)
        {
            var normalized = NormalizeIdentifier(value);
            if (normalized == null)
                return null;

            var parts = normalized.Split('-');
            if (parts.Length != 3 ||
                !int.TryParse(parts[0], out var first) ||
                !int.TryParse(parts[2], out var third))
            {
                return null;
            }

            // Without a court label, reject tokens that are also valid common
            // DD-MM-YY or YYYY-MM-DD dates. Ambiguous low sequence numbers stay
            // observer-false-negative rather than becoming noisy evidence.
            var isDayMonthYear = parts[0].Length <= 2 && first is >= 1 and <= 31;
            var isYearMonthDay = parts[0].Length == 4 &&
                                 first is >= 1900 and <= 2099 &&
                                 third is >= 1 and <= 31;
            return isDayMonthYear || isYearMonthDay ? null : normalized;
        }

        private static IReadOnlyList<EmailEvidenceValue> OfType(
            IEnumerable<EmailEvidenceValue> values,
            EmailEvidenceType evidenceType)
            => values.Where(value => value.EvidenceType == evidenceType).ToArray();

        private static Regex CreateRegex(
            string pattern,
            RegexOptions extraOptions = RegexOptions.None)
            => new(
                pattern,
                RegexOptions.CultureInvariant | RegexOptions.Compiled | extraOptions,
                RegexTimeout);
    }
}
