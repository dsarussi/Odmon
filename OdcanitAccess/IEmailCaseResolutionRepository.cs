using Microsoft.EntityFrameworkCore;
using Odmon.Worker.Models;
using Odmon.Worker.Services;

namespace Odmon.Worker.OdcanitAccess
{
    public interface IEmailCaseResolutionRepository
    {
        Task<IReadOnlyList<EvidenceResolutionResult>> ResolveInternalTikNumbersAsync(
            IEnumerable<string> normalizedValues,
            CancellationToken cancellationToken);

        Task<IReadOnlyList<EvidenceResolutionResult>> ResolveClaimNumbersAsync(
            IEnumerable<string> normalizedValues,
            CancellationToken cancellationToken);

        Task<IReadOnlyList<EvidenceResolutionResult>> ResolveCourtCaseNumbersAsync(
            IEnumerable<string> normalizedValues,
            CancellationToken cancellationToken);

        Task<IReadOnlySet<int>> FilterCandidatesByVehicleAsync(
            IEnumerable<int> candidateTikCounters,
            IEnumerable<string> normalizedValues,
            CancellationToken cancellationToken);

        Task<IReadOnlySet<int>> FilterCandidatesByEventDateAsync(
            IEnumerable<int> candidateTikCounters,
            IEnumerable<string> normalizedValues,
            CancellationToken cancellationToken);

        Task<IReadOnlySet<int>> FilterCandidatesByClientAsync(
            IEnumerable<int> candidateTikCounters,
            IEnumerable<string> normalizedValues,
            CancellationToken cancellationToken);

        Task<IReadOnlySet<int>> FilterCandidatesByInsuredNameAsync(
            IEnumerable<int> candidateTikCounters,
            IEnumerable<string> normalizedValues,
            CancellationToken cancellationToken);

        Task<IReadOnlySet<int>> FilterCandidatesByDriverPhoneAsync(
            IEnumerable<int> candidateTikCounters,
            IEnumerable<string> normalizedValues,
            CancellationToken cancellationToken);
    }

    /// <summary>
    /// EmailFiling-specific, read-only exact lookups over the verified Hozlap
    /// main-data view. Every lookup retains all distinct TikCounters.
    /// </summary>
    public sealed class SqlEmailCaseResolutionRepository(OdcanitDbContext db)
        : IEmailCaseResolutionRepository
    {
        private const string LegalUserDataPageName = "פרטי תיק נזיקין מליגל";
        private static readonly HashSet<string> VehicleFieldNames =
            new(StringComparer.Ordinal) { "מספר רישוי", "מספר רישוי." };
        private static readonly HashSet<string> EventDateFieldNames =
            new(StringComparer.Ordinal) { "תאריך אירוע" };
        private static readonly HashSet<string> InsuredNameFieldNames =
            new(StringComparer.Ordinal) { "שם בעל פוליסה" };
        private static readonly HashSet<string> DriverPhoneFieldNames =
            new(StringComparer.Ordinal) { "סלולרי נהג", "Driver: phone" };

        public Task<IReadOnlyList<EvidenceResolutionResult>> ResolveInternalTikNumbersAsync(
            IEnumerable<string> normalizedValues,
            CancellationToken cancellationToken)
            => ResolveAsync(
                EmailEvidenceType.InternalTikNumber,
                normalizedValues,
                HozlapResolutionField.VisualId,
                cancellationToken);

        public Task<IReadOnlyList<EvidenceResolutionResult>> ResolveClaimNumbersAsync(
            IEnumerable<string> normalizedValues,
            CancellationToken cancellationToken)
            => ResolveAsync(
                EmailEvidenceType.ClaimNumber,
                normalizedValues,
                HozlapResolutionField.Additional,
                cancellationToken);

        public Task<IReadOnlyList<EvidenceResolutionResult>> ResolveCourtCaseNumbersAsync(
            IEnumerable<string> normalizedValues,
            CancellationToken cancellationToken)
            => ResolveAsync(
                EmailEvidenceType.CourtCaseNumber,
                normalizedValues,
                HozlapResolutionField.CourtCaseNumber,
                cancellationToken);

        public Task<IReadOnlySet<int>> FilterCandidatesByVehicleAsync(
            IEnumerable<int> candidateTikCounters,
            IEnumerable<string> normalizedValues,
            CancellationToken cancellationToken)
            => FilterUserDataCandidatesAsync(
                candidateTikCounters,
                normalizedValues,
                VehicleFieldNames,
                static row => row.StringValue == null
                    ? null
                    : EmailEvidenceNormalization.VehicleNumber(row.StringValue),
                EmailEvidenceNormalization.VehicleNumber,
                cancellationToken);

        public Task<IReadOnlySet<int>> FilterCandidatesByEventDateAsync(
            IEnumerable<int> candidateTikCounters,
            IEnumerable<string> normalizedValues,
            CancellationToken cancellationToken)
            => FilterUserDataCandidatesAsync(
                candidateTikCounters,
                normalizedValues,
                EventDateFieldNames,
                static row => row.DateValue.HasValue
                    ? EmailEvidenceNormalization.EventDate(row.DateValue.Value)
                    : row.StringValue == null
                        ? null
                        : EmailEvidenceNormalization.EventDate(row.StringValue),
                EmailEvidenceNormalization.EventDate,
                cancellationToken);

        public async Task<IReadOnlySet<int>> FilterCandidatesByClientAsync(
            IEnumerable<int> candidateTikCounters,
            IEnumerable<string> normalizedValues,
            CancellationToken cancellationToken)
        {
            var candidates = NormalizeCandidateCounters(candidateTikCounters);
            var values = NormalizeSupportingValues(
                normalizedValues,
                static value => EmailEvidenceNormalization.Text(value));
            if (candidates.Count == 0 || values.Count == 0)
                return new HashSet<int>();

            // The join is verified, but no business-client SideType contract is
            // yet proven. This exact VisualID capability remains unused while
            // ClientHints extraction is intentionally empty.
            var rows = await db.SideDataLinks
                .AsNoTracking()
                .Where(side =>
                    candidates.Contains(side.TikCounter) &&
                    side.SideDataCounter.HasValue)
                .Join(
                    db.Clients.AsNoTracking(),
                    side => side.SideDataCounter,
                    client => (int?)client.SideCounter,
                    (side, client) => new ClientResolutionRow(
                        side.TikCounter,
                        client.VisualID))
                .ToListAsync(cancellationToken);

            return rows
                .Where(row =>
                {
                    var normalized = EmailEvidenceNormalization.Text(row.VisualId);
                    return normalized != null && values.Contains(normalized);
                })
                .Select(row => row.TikCounter)
                .Where(candidates.Contains)
                .ToHashSet();
        }

        public Task<IReadOnlySet<int>> FilterCandidatesByInsuredNameAsync(
            IEnumerable<int> candidateTikCounters,
            IEnumerable<string> normalizedValues,
            CancellationToken cancellationToken)
            => FilterUserDataCandidatesAsync(
                candidateTikCounters,
                normalizedValues,
                InsuredNameFieldNames,
                static row => row.StringValue == null
                    ? null
                    : EmailEvidenceNormalization.Text(row.StringValue),
                static value => EmailEvidenceNormalization.Text(value),
                cancellationToken);

        public Task<IReadOnlySet<int>> FilterCandidatesByDriverPhoneAsync(
            IEnumerable<int> candidateTikCounters,
            IEnumerable<string> normalizedValues,
            CancellationToken cancellationToken)
            => FilterUserDataCandidatesAsync(
                candidateTikCounters,
                normalizedValues,
                DriverPhoneFieldNames,
                static row => row.StringValue == null
                    ? null
                    : EmailEvidenceNormalization.Phone(row.StringValue),
                EmailEvidenceNormalization.Phone,
                cancellationToken);

        private async Task<IReadOnlyList<EvidenceResolutionResult>> ResolveAsync(
            EmailEvidenceType evidenceType,
            IEnumerable<string> normalizedValues,
            HozlapResolutionField field,
            CancellationToken cancellationToken)
        {
            var values = NormalizeValues(normalizedValues);
            if (values.Count == 0)
                return [];

            var rows = field switch
            {
                HozlapResolutionField.VisualId => await db.HozlapMainData
                    .AsNoTracking()
                    .Where(row =>
                        row.VisualId != null &&
                        values.Contains(row.VisualId.Trim()))
                    .Select(row => new ResolutionMatch(row.VisualId!, row.TikCounter))
                    .ToListAsync(cancellationToken),
                HozlapResolutionField.Additional => await db.HozlapMainData
                    .AsNoTracking()
                    .Where(row =>
                        row.Additional != null &&
                        values.Contains(row.Additional.Trim()))
                    .Select(row => new ResolutionMatch(row.Additional!, row.TikCounter))
                    .ToListAsync(cancellationToken),
                HozlapResolutionField.CourtCaseNumber => await db.HozlapMainData
                    .AsNoTracking()
                    .Where(row =>
                        row.clcCourtTikNum != null &&
                        values.Contains(row.clcCourtTikNum.Trim()))
                    .Select(row => new ResolutionMatch(row.clcCourtTikNum!, row.TikCounter))
                    .ToListAsync(cancellationToken),
                _ => throw new ArgumentOutOfRangeException(nameof(field))
            };

            return values
                .Select(value => new EvidenceResolutionResult(
                    evidenceType,
                    value,
                    rows
                        .Where(row => string.Equals(
                            row.Value.Trim(),
                            value,
                            StringComparison.Ordinal))
                        .Select(row => row.TikCounter)))
                .ToArray();
        }

        internal static IReadOnlyList<string> NormalizeValues(IEnumerable<string>? values)
            => values?
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => value.Trim())
                .Distinct(StringComparer.Ordinal)
                .ToArray() ?? [];

        private async Task<IReadOnlySet<int>> FilterUserDataCandidatesAsync(
            IEnumerable<int> candidateTikCounters,
            IEnumerable<string> normalizedValues,
            IReadOnlySet<string> fieldNames,
            Func<UserDataResolutionRow, string?> normalizeRowValue,
            Func<string, string?> normalizeInputValue,
            CancellationToken cancellationToken)
        {
            var candidates = NormalizeCandidateCounters(candidateTikCounters);
            var values = NormalizeSupportingValues(normalizedValues, normalizeInputValue);
            if (candidates.Count == 0 || values.Count == 0)
                return new HashSet<int>();

            // Candidate TikCounters are the only SQL authority boundary here.
            // Field/page normalization stays in memory over this bounded batch.
            var rows = await db.UserData
                .AsNoTracking()
                .Where(row => candidates.Contains(row.TikCounter))
                .Select(row => new UserDataResolutionRow(
                    row.TikCounter,
                    row.PageName,
                    row.FieldName,
                    row.strData,
                    row.dateData))
                .ToListAsync(cancellationToken);

            return rows
                .Where(row =>
                    string.Equals(
                        NormalizePageName(row.PageName),
                        LegalUserDataPageName,
                        StringComparison.Ordinal) &&
                    fieldNames.Contains(NormalizeFieldName(row.FieldName) ?? string.Empty))
                .Where(row =>
                {
                    var normalized = normalizeRowValue(row);
                    return normalized != null && values.Contains(normalized);
                })
                .Select(row => row.TikCounter)
                .Where(candidates.Contains)
                .ToHashSet();
        }

        private static IReadOnlySet<string> NormalizeSupportingValues(
            IEnumerable<string>? values,
            Func<string, string?> normalize)
            => values?
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(normalize)
                .Where(value => value != null)
                .Select(value => value!)
                .ToHashSet(StringComparer.Ordinal) ?? new HashSet<string>(StringComparer.Ordinal);

        private static IReadOnlySet<int> NormalizeCandidateCounters(
            IEnumerable<int>? counters)
            => counters?
                .Where(counter => counter > 0)
                .ToHashSet() ?? new HashSet<int>();

        private static string? NormalizeFieldName(string? value)
            => string.IsNullOrWhiteSpace(value)
                ? null
                : value
                    .Replace('’', '\'')
                    .Replace('״', '"')
                    .Trim();

        private static string? NormalizePageName(string? value)
            => string.IsNullOrWhiteSpace(value)
                ? null
                : string.Join(
                    " ",
                    value.Replace('\u00A0', ' ')
                        .Trim()
                        .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

        private sealed record ResolutionMatch(string Value, int TikCounter);
        private sealed record UserDataResolutionRow(
            int TikCounter,
            string? PageName,
            string? FieldName,
            string? StringValue,
            DateTime? DateValue);
        private sealed record ClientResolutionRow(int TikCounter, string VisualId);

        private enum HozlapResolutionField
        {
            VisualId,
            Additional,
            CourtCaseNumber
        }
    }
}
