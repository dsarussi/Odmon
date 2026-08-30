using Odmon.Worker.Models;

namespace Odmon.Worker.Services
{
    [Flags]
    internal enum PhantomPrimaryEvidenceMask
    {
        None = 0,
        InternalTikNumber = 1,
        ClaimNumber = 2,
        CourtCaseNumber = 4
    }

    [Flags]
    internal enum PhantomSupportingEvidenceMask
    {
        None = 0,
        VehicleNumber = 1,
        EventDate = 2,
        ClientHint = 4,
        InsuredName = 8,
        DriverPhone = 16
    }

    internal sealed record EmailFilingResolutionTimings(
        long ExtractionDurationMs,
        long PrimaryResolutionDurationMs,
        long SupportingNarrowingDurationMs,
        long TotalPhantomDurationMs);

    internal static class EmailFilingResolutionTelemetry
    {
        public static EmailFilingResolutionRun CreateRun(
            EmailCaseEvidence? evidence,
            EmailCaseResolutionSnapshot snapshot,
            EmailCaseResolutionAnalysis analysis,
            IEnumerable<int> existingAuthorityTikCounters,
            EmailFilingResolutionTimings timings,
            string? observerErrorCategory,
            DateTime createdAtUtc)
        {
            var existing = existingAuthorityTikCounters
                .Where(counter => counter > 0)
                .ToHashSet();
            var phantom = string.IsNullOrWhiteSpace(observerErrorCategory)
                ? SelectWouldFileTargets(snapshot, analysis)
                : new HashSet<int>();
            var run = new EmailFilingResolutionRun
            {
                ExistingAuthorityTargetCount = existing.Count,
                PhantomTargetCount = phantom.Count,
                FinalResolutionClass = ClassifyFinal(evidence, snapshot, analysis, phantom, observerErrorCategory),
                PrimaryEvidenceTypeMask = (int)GetPrimaryMask(evidence),
                SupportingEvidenceTypeMask = (int)GetSupportingMask(evidence),
                TikPrimaryValueCount = evidence?.InternalTikNumbers.Count ?? 0,
                ClaimPrimaryValueCount = evidence?.ClaimNumbers.Count ?? 0,
                CourtPrimaryValueCount = evidence?.CourtCaseNumbers.Count ?? 0,
                TikCandidateCounterCount = snapshot.TikResults.Sum(result => result.TikCounters.Count),
                ClaimCandidateCounterCount = snapshot.ClaimResults.Sum(result => result.TikCounters.Count),
                CourtCandidateCounterCount = snapshot.CourtResults.Sum(result => result.TikCounters.Count),
                NarrowingApplied = analysis.ObserverClassifications.Any(value =>
                    value.StartsWith("NARROWED_BY_", StringComparison.Ordinal)),
                AgreementWithExistingAuthority = ClassifyAgreement(existing, phantom),
                ExtractionDurationMs = timings.ExtractionDurationMs,
                PrimaryResolutionDurationMs = timings.PrimaryResolutionDurationMs,
                SupportingNarrowingDurationMs = timings.SupportingNarrowingDurationMs,
                TotalPhantomDurationMs = timings.TotalPhantomDurationMs,
                ObserverErrorCategory = string.IsNullOrWhiteSpace(observerErrorCategory)
                    ? null
                    : observerErrorCategory,
                CreatedAtUtc = createdAtUtc
            };

            run.Targets.AddRange(existing.Select(counter => new EmailFilingResolutionTarget
            {
                TikCounter = counter,
                TargetKind = EmailFilingResolutionTargetKinds.ExistingAuthority
            }));
            run.Targets.AddRange(phantom.Select(counter => new EmailFilingResolutionTarget
            {
                TikCounter = counter,
                TargetKind = EmailFilingResolutionTargetKinds.PhantomWouldFile
            }));
            return run;
        }

        internal static string ClassifyAgreement(
            IReadOnlySet<int> existingAuthority,
            IReadOnlySet<int> phantom)
        {
            if (existingAuthority.Count == 0)
                return EmailFilingResolutionAgreements.NoExistingTikAuthority;
            if (phantom.Count == 0)
                return EmailFilingResolutionAgreements.PhantomUnresolved;
            if (existingAuthority.SetEquals(phantom))
                return EmailFilingResolutionAgreements.ExactAgreement;
            if (existingAuthority.IsSubsetOf(phantom))
                return EmailFilingResolutionAgreements.PhantomSuperset;
            if (phantom.IsSubsetOf(existingAuthority))
                return EmailFilingResolutionAgreements.PhantomSubset;
            if (existingAuthority.Overlaps(phantom))
                return EmailFilingResolutionAgreements.PartialOverlap;
            return EmailFilingResolutionAgreements.Disjoint;
        }

        internal static IReadOnlySet<int> SelectWouldFileTargets(
            EmailCaseResolutionSnapshot snapshot,
            EmailCaseResolutionAnalysis analysis)
        {
            if (analysis.PrimaryResults.Any(result => result.Status == PrimaryNarrowingStatus.Conflict))
                return new HashSet<int>();

            var usable = analysis.PrimaryResults
                .Where(result => result.PrimaryResult.TikCounters.Count > 0)
                .ToArray();
            if (usable.Length == 0 || usable.Any(result => result.Status != PrimaryNarrowingStatus.Unique))
                return new HashSet<int>();

            var uniqueTikResults = usable
                .Where(result => result.PrimaryResult.EvidenceType == EmailEvidenceType.InternalTikNumber)
                .ToArray();
            if (uniqueTikResults.Length > 1)
            {
                var tikTargets = uniqueTikResults
                    .SelectMany(result => result.RemainingTikCounters)
                    .ToHashSet();
                var otherTargets = usable
                    .Except(uniqueTikResults)
                    .SelectMany(result => result.RemainingTikCounters);
                return otherTargets.All(tikTargets.Contains)
                    ? tikTargets
                    : new HashSet<int>();
            }

            var singletons = usable
                .Select(result => result.RemainingTikCounters.Single())
                .ToHashSet();
            return singletons.Count == 1 ? singletons : new HashSet<int>();
        }

        private static string ClassifyFinal(
            EmailCaseEvidence? evidence,
            EmailCaseResolutionSnapshot snapshot,
            EmailCaseResolutionAnalysis analysis,
            IReadOnlySet<int> phantomTargets,
            string? observerErrorCategory)
        {
            if (!string.IsNullOrWhiteSpace(observerErrorCategory))
                return EmailFilingResolutionClasses.ObserverError;

            var primaryValueCount = (evidence?.InternalTikNumbers.Count ?? 0) +
                                    (evidence?.ClaimNumbers.Count ?? 0) +
                                    (evidence?.CourtCaseNumbers.Count ?? 0);
            if (primaryValueCount == 0)
                return EmailFilingResolutionClasses.NoPrimary;

            var results = analysis.PrimaryResults;
            if (results.Count == 0 || results.All(result => result.Status == PrimaryNarrowingStatus.NotFound))
                return EmailFilingResolutionClasses.PrimaryNotFound;
            if (results.Any(result => result.Status == PrimaryNarrowingStatus.Conflict) ||
                analysis.ObserverClassifications.Contains("SUPPORTING_EVIDENCE_CONFLICT", StringComparer.Ordinal))
                return EmailFilingResolutionClasses.SupportingConflict;
            if (phantomTargets.Count > 1 && snapshot.TikResults.Count > 1)
                return EmailFilingResolutionClasses.MultiTik;
            if (analysis.ObserverClassifications.Contains("PRIMARY_CONFLICT", StringComparer.Ordinal))
                return EmailFilingResolutionClasses.PrimaryConflict;
            if (analysis.ObserverClassifications.Contains("PARTIAL_OVERLAP", StringComparer.Ordinal))
                return EmailFilingResolutionClasses.PartialOverlap;

            var narrowed = analysis.ObserverClassifications.Any(value =>
                value.StartsWith("NARROWED_BY_", StringComparison.Ordinal));
            if (phantomTargets.Count == 1 && narrowed &&
                results.Any(result => result.PrimaryResult.Status == EvidenceResolutionStatus.Ambiguous))
                return EmailFilingResolutionClasses.NarrowedToUnique;
            if (results.Any(result => result.Status == PrimaryNarrowingStatus.Ambiguous))
                return narrowed
                    ? EmailFilingResolutionClasses.NarrowedStillAmbiguous
                    : EmailFilingResolutionClasses.PrimaryAmbiguous;
            if (phantomTargets.Count == 1 && results.Count(result =>
                    result.PrimaryResult.TikCounters.Count > 0) > 1)
                return EmailFilingResolutionClasses.PrimaryAgreement;
            if (phantomTargets.Count == 1)
                return EmailFilingResolutionClasses.PrimaryUnique;
            return EmailFilingResolutionClasses.PrimaryConflict;
        }

        private static PhantomPrimaryEvidenceMask GetPrimaryMask(EmailCaseEvidence? evidence)
        {
            var mask = PhantomPrimaryEvidenceMask.None;
            if (evidence?.InternalTikNumbers.Count > 0) mask |= PhantomPrimaryEvidenceMask.InternalTikNumber;
            if (evidence?.ClaimNumbers.Count > 0) mask |= PhantomPrimaryEvidenceMask.ClaimNumber;
            if (evidence?.CourtCaseNumbers.Count > 0) mask |= PhantomPrimaryEvidenceMask.CourtCaseNumber;
            return mask;
        }

        private static PhantomSupportingEvidenceMask GetSupportingMask(EmailCaseEvidence? evidence)
        {
            var mask = PhantomSupportingEvidenceMask.None;
            if (evidence?.VehicleNumbers.Count > 0) mask |= PhantomSupportingEvidenceMask.VehicleNumber;
            if (evidence?.EventDates.Count > 0) mask |= PhantomSupportingEvidenceMask.EventDate;
            if (evidence?.ClientHints.Count > 0) mask |= PhantomSupportingEvidenceMask.ClientHint;
            if (evidence?.InsuredNames.Count > 0) mask |= PhantomSupportingEvidenceMask.InsuredName;
            if (evidence?.DriverPhones.Count > 0) mask |= PhantomSupportingEvidenceMask.DriverPhone;
            return mask;
        }
    }
}
