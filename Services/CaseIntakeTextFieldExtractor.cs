using System.Text.RegularExpressions;
using Odmon.Worker.Models;

namespace Odmon.Worker.Services
{
    internal enum RawFieldExtractionStatus
    {
        Found,
        Missing,
        Ambiguous
    }

    internal sealed record RawFieldMatch(string Label, string Value);

    internal sealed record RawFieldExtraction(
        RawFieldExtractionStatus Status,
        string? Label,
        string? RawValue,
        IReadOnlyList<RawFieldMatch> Matches);

    internal sealed partial class CaseIntakeTextFieldExtractor
    {
        private readonly string[] _allLabels;

        public CaseIntakeTextFieldExtractor(IEnumerable<string> allLabels)
        {
            _allLabels = allLabels
                .Distinct(StringComparer.Ordinal)
                .OrderByDescending(label => label.Length)
                .ToArray();
        }

        [GeneratedRegex(@"[\u200E\u200F\u202A-\u202E\u2066-\u2069]", RegexOptions.CultureInvariant)]
        private static partial Regex DirectionMarkRegex();

        public IReadOnlyList<string> NormalizeLines(string text)
            => text
                .Replace("\r\n", "\n", StringComparison.Ordinal)
                .Replace('\r', '\n')
                .Split('\n')
                .Select(line => DirectionMarkRegex().Replace(line, string.Empty).Trim())
                .Where(line => line.Length > 0)
                .ToArray();

        public RawFieldExtraction Extract(
            IReadOnlyList<string> lines,
            IReadOnlyList<string> labels)
        {
            var matches = ExtractAll(lines, labels)
                .GroupBy(match => match.Value, StringComparer.Ordinal)
                .Select(group => group.First())
                .ToArray();

            var matchedEmptyLabel = FindEmptyLabel(lines, labels);
            return matches.Length switch
            {
                0 => new(
                    RawFieldExtractionStatus.Missing,
                    matchedEmptyLabel,
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
        }

        public IReadOnlyList<RawFieldMatch> ExtractAll(
            IReadOnlyList<string> lines,
            IReadOnlyList<string> labels)
        {
            var matches = new List<RawFieldMatch>();
            for (var lineIndex = 0; lineIndex < lines.Count; lineIndex++)
            {
                var line = lines[lineIndex];
                foreach (var label in labels.OrderByDescending(value => value.Length))
                {
                    var searchStart = 0;
                    while (searchStart < line.Length)
                    {
                        var labelIndex = line.IndexOf(label, searchStart, StringComparison.Ordinal);
                        if (labelIndex < 0)
                        {
                            break;
                        }

                        searchStart = labelIndex + label.Length;
                        if (!HasLabelBoundaries(line, labelIndex, label.Length) ||
                            IsPrefixOfLongerLabelAt(line, label, labelIndex))
                        {
                            continue;
                        }

                        var value = ExtractSameLineValue(line, labelIndex + label.Length);
                        if (value.Length == 0 &&
                            lineIndex + 1 < lines.Count &&
                            !ContainsKnownLabel(lines[lineIndex + 1]))
                        {
                            value = TrimValue(lines[lineIndex + 1]);
                        }

                        if (value.Length > 0)
                        {
                            matches.Add(new RawFieldMatch(label, value));
                        }
                    }
                }
            }

            return matches;
        }

        private string? FindEmptyLabel(
            IReadOnlyList<string> lines,
            IReadOnlyList<string> labels)
        {
            foreach (var line in lines)
            {
                foreach (var label in labels.OrderByDescending(value => value.Length))
                {
                    var index = line.IndexOf(label, StringComparison.Ordinal);
                    if (index >= 0 &&
                        HasLabelBoundaries(line, index, label.Length) &&
                        !IsPrefixOfLongerLabelAt(line, label, index))
                    {
                        return label;
                    }
                }
            }

            return null;
        }

        private string ExtractSameLineValue(string line, int valueStart)
        {
            var valueEnd = line.Length;
            foreach (var label in _allLabels)
            {
                var nextLabel = line.IndexOf(label, valueStart, StringComparison.Ordinal);
                if (nextLabel >= 0 &&
                    HasLabelBoundaries(line, nextLabel, label.Length) &&
                    nextLabel < valueEnd)
                {
                    valueEnd = nextLabel;
                }
            }

            return TrimValue(line[valueStart..valueEnd]);
        }

        private static string TrimValue(string value)
        {
            var trimmed = value.Trim().TrimStart(':', '|').Trim();
            if (trimmed.Length > 1 &&
                trimmed[0] is '-' or '–' or '—' &&
                char.IsWhiteSpace(trimmed[1]))
            {
                return trimmed[1..].Trim();
            }

            return trimmed;
        }

        private bool ContainsKnownLabel(string line)
            => _allLabels.Any(label =>
            {
                var index = line.IndexOf(label, StringComparison.Ordinal);
                return index >= 0 && HasLabelBoundaries(line, index, label.Length);
            });

        private bool IsPrefixOfLongerLabelAt(string line, string label, int labelIndex)
            => _allLabels.Any(candidate =>
                candidate.Length > label.Length &&
                candidate.StartsWith(label, StringComparison.Ordinal) &&
                labelIndex + candidate.Length <= line.Length &&
                line.AsSpan(labelIndex, candidate.Length).SequenceEqual(candidate));

        private static bool HasLabelBoundaries(string line, int labelIndex, int labelLength)
        {
            var hasValidStart = labelIndex == 0 || !char.IsLetterOrDigit(line[labelIndex - 1]);
            var end = labelIndex + labelLength;
            var hasValidEnd = end == line.Length || !char.IsLetterOrDigit(line[end]);
            return hasValidStart && hasValidEnd;
        }
    }

    internal static class CaseIntakeFieldFactory
    {
        public static CaseIntakeField<TValue> Build<TValue>(
            RawFieldExtraction extraction,
            OdcanitCaseDocument document,
            Func<string, FieldValidationResult<TValue>> validate)
        {
            if (extraction.Status == RawFieldExtractionStatus.Missing)
            {
                return new(
                    default!,
                    CaseIntakeFieldStatus.Missing,
                    document.Id,
                    document.Name,
                    extraction.Label,
                    null,
                    "Field was not found or had no value.");
            }

            if (extraction.Status == RawFieldExtractionStatus.Ambiguous)
            {
                return new(
                    default!,
                    CaseIntakeFieldStatus.Ambiguous,
                    document.Id,
                    document.Name,
                    extraction.Label,
                    extraction.RawValue,
                    "Multiple distinct values were found in the source document.");
            }

            var validation = validate(extraction.RawValue!);
            return new(
                validation.Value,
                validation.Status,
                document.Id,
                document.Name,
                extraction.Label,
                extraction.RawValue,
                validation.Message);
        }
    }

    internal static class CaseIntakeClaimNumberResolver
    {
        public static CaseIntakeField<string?> Resolve(
            OdcanitCaseDocument document,
            RawFieldExtraction explicitClaim,
            RawFieldExtraction alternativeClaim)
        {
            var explicitValid = GetValidCandidates(explicitClaim);
            var alternativeValid = GetValidCandidates(alternativeClaim);

            if (explicitValid.Count > 1)
            {
                return Ambiguous(document, explicitValid.Concat(alternativeValid));
            }

            if (explicitValid.Count == 1)
            {
                var explicitValue = explicitValid[0];
                if (alternativeValid.Any(candidate =>
                        !string.Equals(
                            candidate.Value,
                            explicitValue.Value,
                            StringComparison.OrdinalIgnoreCase)))
                {
                    return Ambiguous(document, explicitValid.Concat(alternativeValid));
                }

                return Valid(document, explicitValue);
            }

            if (alternativeValid.Count > 1)
            {
                return Ambiguous(document, alternativeValid);
            }

            if (alternativeValid.Count == 1)
            {
                return Valid(document, alternativeValid[0]);
            }

            return explicitClaim.Status != RawFieldExtractionStatus.Missing
                ? CaseIntakeFieldFactory.Build(
                    explicitClaim,
                    document,
                    raw => CaseIntakeFieldValidators.ValidateNumber(raw, "Claim number"))
                : CaseIntakeFieldFactory.Build(
                    alternativeClaim,
                    document,
                    raw => CaseIntakeFieldValidators.ValidateNumber(raw, "Claim number"));
        }

        private static IReadOnlyList<ValidClaimCandidate> GetValidCandidates(
            RawFieldExtraction extraction)
            => extraction.Matches
                .Select(match => new
                {
                    Match = match,
                    Validation = CaseIntakeFieldValidators.ValidateNumber(match.Value, "Claim number")
                })
                .Where(candidate =>
                    candidate.Validation.Status == CaseIntakeFieldStatus.Valid &&
                    candidate.Validation.Value != null)
                .Select(candidate => new ValidClaimCandidate(
                    candidate.Validation.Value!,
                    candidate.Match.Label,
                    candidate.Match.Value))
                .GroupBy(candidate => candidate.Value, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .ToArray();

        private static CaseIntakeField<string?> Valid(
            OdcanitCaseDocument document,
            ValidClaimCandidate candidate)
            => new(
                candidate.Value,
                CaseIntakeFieldStatus.Valid,
                document.Id,
                document.Name,
                candidate.Label,
                candidate.RawValue,
                null);

        private static CaseIntakeField<string?> Ambiguous(
            OdcanitCaseDocument document,
            IEnumerable<ValidClaimCandidate> candidates)
        {
            var distinct = candidates
                .GroupBy(candidate => candidate.Value, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .ToArray();
            return new(
                null,
                CaseIntakeFieldStatus.Ambiguous,
                document.Id,
                document.Name,
                string.Join(", ", distinct.Select(candidate => candidate.Label).Distinct(StringComparer.Ordinal)),
                string.Join(" | ", distinct.Select(candidate => $"{candidate.Label}={candidate.RawValue}")),
                "Conflicting valid claim numbers were found in the source document.");
        }

        private sealed record ValidClaimCandidate(
            string Value,
            string Label,
            string RawValue);
    }
}
