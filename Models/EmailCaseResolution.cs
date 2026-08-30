namespace Odmon.Worker.Models
{
    public enum EvidenceResolutionStatus
    {
        NotFound,
        Unique,
        Ambiguous
    }

    public enum PrimaryNarrowingStatus
    {
        NotFound,
        Unique,
        Ambiguous,
        Conflict
    }

    /// <summary>
    /// Transient set-valued result for one normalized evidence value. The value
    /// itself may contain sensitive operational data and is not diagnostic data.
    /// </summary>
    public sealed record EvidenceResolutionResult
    {
        public EvidenceResolutionResult(
            EmailEvidenceType evidenceType,
            string normalizedValue,
            IEnumerable<int> tikCounters)
        {
            EvidenceType = evidenceType;
            NormalizedValue = normalizedValue;
            TikCounters = tikCounters
                .Where(counter => counter > 0)
                .Distinct()
                .OrderBy(counter => counter)
                .ToArray();
            Status = TikCounters.Count switch
            {
                0 => EvidenceResolutionStatus.NotFound,
                1 => EvidenceResolutionStatus.Unique,
                _ => EvidenceResolutionStatus.Ambiguous
            };
        }

        public EmailEvidenceType EvidenceType { get; }
        public string NormalizedValue { get; }
        public IReadOnlyList<int> TikCounters { get; }
        public EvidenceResolutionStatus Status { get; }
    }

    public sealed record EmailCaseResolutionSnapshot(
        IReadOnlyList<EvidenceResolutionResult> TikResults,
        IReadOnlyList<EvidenceResolutionResult> ClaimResults,
        IReadOnlyList<EvidenceResolutionResult> CourtResults)
    {
        public static EmailCaseResolutionSnapshot Empty { get; } = new([], [], []);

        /// <summary>
        /// Returns privacy-safe aggregate observer classifications only. Raw
        /// normalized evidence values are deliberately excluded.
        /// </summary>
        public IReadOnlyList<string> GetObserverClassifications(
            bool includePrimaryRelationship = true)
        {
            var classifications = new List<string>();
            AddStatusClassifications(classifications, "TIK", TikResults);
            AddStatusClassifications(classifications, "CLAIM", ClaimResults);
            AddStatusClassifications(classifications, "COURT", CourtResults);

            if (includePrimaryRelationship)
                AddPrimaryRelationship(classifications, AllResults());

            return classifications.Distinct(StringComparer.Ordinal).ToArray();
        }

        internal IEnumerable<EvidenceResolutionResult> AllResults()
            => TikResults.Concat(ClaimResults).Concat(CourtResults);

        internal static void AddPrimaryRelationship(
            ICollection<string> classifications,
            IEnumerable<EvidenceResolutionResult> results)
        {
            var primarySets = results
                .Where(result => result.TikCounters.Count > 0)
                .Select(result => result.TikCounters.ToHashSet())
                .ToArray();
            AddPrimaryRelationship(classifications, primarySets);
        }

        internal static void AddPrimaryRelationship(
            ICollection<string> classifications,
            IReadOnlyList<HashSet<int>> primarySets)
        {
            if (primarySets.Count < 2)
                return;

            var allEqual = primarySets
                .Skip(1)
                .All(set => primarySets[0].SetEquals(set));
            var hasDisjointPair = primarySets
                .SelectMany(
                    (left, index) => primarySets.Skip(index + 1)
                        .Select(right => !left.Overlaps(right)))
                .Any(disjoint => disjoint);

            classifications.Add(allEqual
                ? "PRIMARY_AGREEMENT"
                : hasDisjointPair
                    ? "PRIMARY_CONFLICT"
                    : "PARTIAL_OVERLAP");
        }

        private static void AddStatusClassifications(
            ICollection<string> destination,
            string prefix,
            IEnumerable<EvidenceResolutionResult> results)
        {
            foreach (var status in results.Select(result => result.Status).Distinct())
            {
                destination.Add($"{prefix}_{status switch
                {
                    EvidenceResolutionStatus.NotFound => "NOT_FOUND",
                    EvidenceResolutionStatus.Unique => "UNIQUE",
                    EvidenceResolutionStatus.Ambiguous => "AMBIGUOUS",
                    _ => throw new ArgumentOutOfRangeException(nameof(status))
                }}");
            }
        }
    }

    public sealed record PrimaryNarrowingResult(
        EvidenceResolutionResult PrimaryResult,
        IReadOnlyList<int> RemainingTikCounters,
        PrimaryNarrowingStatus Status,
        IReadOnlyList<string> Classifications);

    /// <summary>
    /// Transient Slice 3 observer analysis. It is intentionally disconnected
    /// from EmailFiling target authority and persistence models.
    /// </summary>
    public sealed record EmailCaseResolutionAnalysis(
        EmailCaseResolutionSnapshot PrimarySnapshot,
        IReadOnlyList<PrimaryNarrowingResult> PrimaryResults,
        IReadOnlyList<string> ObserverClassifications)
    {
        public static EmailCaseResolutionAnalysis Empty { get; } =
            new(EmailCaseResolutionSnapshot.Empty, [], []);

        public static EmailCaseResolutionAnalysis WithoutSupportingNarrowing(
            EmailCaseResolutionSnapshot snapshot)
        {
            var primaryResults = snapshot.AllResults()
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
            return new EmailCaseResolutionAnalysis(
                snapshot,
                primaryResults,
                snapshot.GetObserverClassifications());
        }
    }
}
