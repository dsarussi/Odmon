using Odmon.Worker.Models;
using Odmon.Worker.OdcanitAccess;

namespace Odmon.Worker.Services
{
    public interface IEmailCaseResolutionEngine
    {
        Task<EmailCaseResolutionAnalysis> AnalyzeAsync(
            EmailCaseEvidence evidence,
            EmailCaseResolutionSnapshot primarySnapshot,
            CancellationToken cancellationToken);
    }

    /// <summary>
    /// Deterministic observer-only narrowing. Supporting evidence can only
    /// intersect an existing primary candidate set and can never expand it.
    /// </summary>
    public sealed class EmailCaseResolutionEngine(IEmailCaseResolutionRepository repository)
        : IEmailCaseResolutionEngine
    {
        public async Task<EmailCaseResolutionAnalysis> AnalyzeAsync(
            EmailCaseEvidence evidence,
            EmailCaseResolutionSnapshot primarySnapshot,
            CancellationToken cancellationToken)
        {
            var primaryResults = primarySnapshot.AllResults().ToArray();
            var candidateUnion = primaryResults
                .SelectMany(result => result.TikCounters)
                .ToHashSet();
            var supportInputs = GetSupportInputs(evidence);
            var classifications = primarySnapshot
                .GetObserverClassifications(includePrimaryRelationship: false)
                .ToList();

            // Without a primary candidate set, supporting evidence never reaches
            // the repository and cannot independently search Odcanit.
            if (candidateUnion.Count == 0)
            {
                if (supportInputs.Any(input => input.Values.Count > 0))
                    classifications.Add("SUPPORTING_EVIDENCE_NO_EFFECT");
                return BuildAnalysis(primarySnapshot, primaryResults, [], classifications);
            }

            if (primarySnapshot.TikResults.Count > 1 ||
                primarySnapshot.ClaimResults.Count > 1 ||
                primarySnapshot.CourtResults.Count > 1)
            {
                // Several values of one primary type can belong to unrelated
                // quoted-thread contexts. Without context groups, do not apply
                // one global supporting intersection to all of them.
                if (supportInputs.Any(input => input.Values.Count > 0))
                    classifications.Add("SUPPORTING_EVIDENCE_NO_EFFECT");
                return BuildAnalysis(primarySnapshot, primaryResults, [], classifications);
            }

            var filters = new List<AppliedSupportFilter>();
            foreach (var input in supportInputs)
            {
                if (input.Values.Count == 0)
                    continue;

                // Until context grouping exists, more than one value of a type
                // is not applied globally across a potentially quoted thread.
                if (input.Values.Count != 1)
                {
                    classifications.Add("SUPPORTING_EVIDENCE_NO_EFFECT");
                    continue;
                }

                var matchingCounters = await FilterCandidatesAsync(
                    input.EvidenceType,
                    candidateUnion,
                    input.Values,
                    cancellationToken);
                filters.Add(new AppliedSupportFilter(
                    input.EvidenceType,
                    input.NarrowingClassification,
                    matchingCounters.ComparableTikCounters.Where(candidateUnion.Contains).ToHashSet(),
                    matchingCounters.MatchingTikCounters.Where(candidateUnion.Contains).ToHashSet()));
            }

            var narrowedResults = primaryResults
                .Select(result => Narrow(result, filters))
                .ToArray();
            foreach (var classification in narrowedResults.SelectMany(result => result.Classifications))
                classifications.Add(classification);

            var comparable = narrowedResults
                .Where(result => result.PrimaryResult.TikCounters.Count > 0)
                .ToArray();
            if (comparable.Length >= 2)
            {
                if (comparable.Any(result => result.RemainingTikCounters.Count == 0))
                {
                    classifications.Add("PRIMARY_CONFLICT");
                }
                else
                {
                    EmailCaseResolutionSnapshot.AddPrimaryRelationship(
                        classifications,
                        comparable
                            .Select(result => result.RemainingTikCounters.ToHashSet())
                            .ToArray());
                }
            }

            return new EmailCaseResolutionAnalysis(
                primarySnapshot,
                narrowedResults,
                classifications.Distinct(StringComparer.Ordinal).ToArray());
        }

        private async Task<SupportingEvidenceFilterResult> FilterCandidatesAsync(
            EmailEvidenceType evidenceType,
            IReadOnlySet<int> candidateTikCounters,
            IReadOnlyList<string> values,
            CancellationToken cancellationToken)
            => evidenceType switch
            {
                EmailEvidenceType.VehicleNumber =>
                    await repository.FilterCandidatesByVehicleAsync(
                        candidateTikCounters,
                        values,
                        cancellationToken),
                EmailEvidenceType.EventDate =>
                    await repository.FilterCandidatesByEventDateAsync(
                        candidateTikCounters,
                        values,
                        cancellationToken),
                EmailEvidenceType.ClientHint =>
                    await repository.FilterCandidatesByClientAsync(
                        candidateTikCounters,
                        values,
                        cancellationToken),
                EmailEvidenceType.InsuredName =>
                    await repository.FilterCandidatesByInsuredNameAsync(
                        candidateTikCounters,
                        values,
                        cancellationToken),
                EmailEvidenceType.DriverPhone =>
                    await repository.FilterCandidatesByDriverPhoneAsync(
                        candidateTikCounters,
                        values,
                        cancellationToken),
                _ => throw new InvalidOperationException(
                    "Only supporting evidence can filter primary candidates.")
            };

        private static PrimaryNarrowingResult Narrow(
            EvidenceResolutionResult primary,
            IReadOnlyList<AppliedSupportFilter> filters)
        {
            if (primary.TikCounters.Count == 0)
            {
                return new PrimaryNarrowingResult(
                    primary,
                    [],
                    PrimaryNarrowingStatus.NotFound,
                    []);
            }

            var remaining = primary.TikCounters.ToHashSet();
            var original = primary.TikCounters.ToHashSet();
            var classifications = new List<string>();
            var consideredFilterCount = 0;
            foreach (var filter in filters)
            {
                // InsuredName is deliberately weak. It may narrow ambiguity but
                // cannot veto a candidate already made unique by stronger evidence.
                if (filter.EvidenceType == EmailEvidenceType.InsuredName && remaining.Count <= 1)
                    continue;

                consideredFilterCount++;
                var countBeforeFilter = remaining.Count;
                var comparableRemaining = remaining
                    .Where(filter.ComparableTikCounters.Contains)
                    .ToHashSet();
                var contradictions = comparableRemaining
                    .Where(counter => !filter.MatchingTikCounters.Contains(counter))
                    .ToHashSet();
                if (contradictions.Count > 0)
                    classifications.Add(filter.NarrowingClassification);
                remaining.ExceptWith(contradictions);
                if (countBeforeFilter > 1 && remaining.Count == 1)
                {
                    classifications.Add(filter.EvidenceType switch
                    {
                        EmailEvidenceType.VehicleNumber => "NARROWED_TO_UNIQUE_BY_VEHICLE",
                        EmailEvidenceType.EventDate => "NARROWED_TO_UNIQUE_BY_EVENT_DATE",
                        EmailEvidenceType.ClientHint => "NARROWED_TO_UNIQUE_BY_CLIENT",
                        EmailEvidenceType.InsuredName => "NARROWED_TO_UNIQUE_BY_INSURED_NAME",
                        EmailEvidenceType.DriverPhone => "NARROWED_TO_UNIQUE_BY_DRIVER_PHONE",
                        _ => throw new ArgumentOutOfRangeException()
                    });
                }
            }
            var changed = remaining.Count < original.Count;

            PrimaryNarrowingStatus status;
            if (remaining.Count == 0)
            {
                status = PrimaryNarrowingStatus.Conflict;
                classifications.Add("SUPPORTING_EVIDENCE_CONFLICT");
            }
            else if (remaining.Count == 1)
            {
                status = PrimaryNarrowingStatus.Unique;
                if (consideredFilterCount > 0 && !changed)
                    classifications.Add("SUPPORTING_EVIDENCE_NO_EFFECT");
            }
            else
            {
                status = PrimaryNarrowingStatus.Ambiguous;
                classifications.Add("PRIMARY_STILL_AMBIGUOUS");
                if (consideredFilterCount > 0 && !changed)
                    classifications.Add("SUPPORTING_EVIDENCE_NO_EFFECT");
            }

            return new PrimaryNarrowingResult(
                primary,
                remaining.OrderBy(counter => counter).ToArray(),
                status,
                classifications.Distinct(StringComparer.Ordinal).ToArray());
        }

        private static IReadOnlyList<SupportInput> GetSupportInputs(EmailCaseEvidence evidence)
            =>
            [
                CreateSupportInput(
                    EmailEvidenceType.VehicleNumber,
                    evidence.VehicleNumbers,
                    "NARROWED_BY_VEHICLE"),
                CreateSupportInput(
                    EmailEvidenceType.EventDate,
                    evidence.EventDates,
                    "NARROWED_BY_EVENT_DATE"),
                CreateSupportInput(
                    EmailEvidenceType.ClientHint,
                    evidence.ClientHints,
                    "NARROWED_BY_CLIENT"),
                CreateSupportInput(
                    EmailEvidenceType.InsuredName,
                    evidence.InsuredNames,
                    "NARROWED_BY_INSURED_NAME"),
                CreateSupportInput(
                    EmailEvidenceType.DriverPhone,
                    evidence.DriverPhones,
                    "NARROWED_BY_DRIVER_PHONE")
            ];

        private static SupportInput CreateSupportInput(
            EmailEvidenceType evidenceType,
            IEnumerable<EmailEvidenceValue> values,
            string narrowingClassification)
            => new(
                evidenceType,
                values
                    .Select(value => value.NormalizedValue)
                    .Distinct(StringComparer.Ordinal)
                    .ToArray(),
                narrowingClassification);

        private static EmailCaseResolutionAnalysis BuildAnalysis(
            EmailCaseResolutionSnapshot snapshot,
            IEnumerable<EvidenceResolutionResult> primaryResults,
            IReadOnlyList<PrimaryNarrowingResult> narrowedResults,
            ICollection<string> classifications)
        {
            var results = narrowedResults.Count > 0
                ? narrowedResults
                : primaryResults
                    .Select(result => new PrimaryNarrowingResult(
                        result,
                        result.TikCounters,
                        result.Status switch
                        {
                            EvidenceResolutionStatus.NotFound => PrimaryNarrowingStatus.NotFound,
                            EvidenceResolutionStatus.Unique => PrimaryNarrowingStatus.Unique,
                            EvidenceResolutionStatus.Ambiguous => PrimaryNarrowingStatus.Ambiguous,
                            _ => throw new ArgumentOutOfRangeException()
                        },
                        []))
                    .ToArray();
            if (narrowedResults.Count == 0)
                EmailCaseResolutionSnapshot.AddPrimaryRelationship(classifications, primaryResults);
            return new EmailCaseResolutionAnalysis(
                snapshot,
                results,
                classifications.Distinct(StringComparer.Ordinal).ToArray());
        }

        private sealed record SupportInput(
            EmailEvidenceType EvidenceType,
            IReadOnlyList<string> Values,
            string NarrowingClassification);

        private sealed record AppliedSupportFilter(
            EmailEvidenceType EvidenceType,
            string NarrowingClassification,
            IReadOnlySet<int> ComparableTikCounters,
            IReadOnlySet<int> MatchingTikCounters);
    }
}
