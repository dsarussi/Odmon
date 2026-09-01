using Odmon.Worker.Models;

namespace Odmon.Worker.Services
{
    internal static class EmailFilingAuthorityKinds
    {
        public const string ExactTik = "EXACT_TIK";
        public const string DirectUniqueClaim = "DIRECT_UNIQUE_CLAIM";
        public const string DirectClaimVehicle = "DIRECT_CLAIM_VEHICLE";
        public const string ExactCourt = "EXACT_COURT";
        public const string BlockedConflict = "BLOCKED_CONFLICT";
        public const string BlockedMultiTik = "BLOCKED_MULTI_TIK";
        public const string BlockedAmbiguous = "BLOCKED_AMBIGUOUS";
        public const string BlockedInsuredDecisive = "BLOCKED_INSURED_DECISIVE";
        public const string BlockedNoSafeAuthority = "BLOCKED_NO_SAFE_AUTHORITY";
        public const string BlockedMultipleCandidates = "BLOCKED_MULTIPLE_CANDIDATES";
        public const string BlockedResolverError = "BLOCKED_RESOLVER_ERROR";
    }

    internal sealed record EmailFilingAuthorityDecision(
        bool IsAuthorized,
        string AuthorityKind,
        int? TikCounter)
    {
        public static EmailFilingAuthorityDecision Blocked(string reason)
            => new(false, reason, null);

        public static EmailFilingAuthorityDecision Authorized(string kind, int tikCounter)
            => new(true, kind, tikCounter);
    }

    /// <summary>
    /// Pure high-confidence authority policy. Resolution can observe many
    /// outcomes, but this gate authorizes exactly one final TikCounter only for
    /// the explicitly approved deterministic cases.
    /// </summary>
    internal static class EmailFilingRealWriteGate
    {
        public static EmailFilingAuthorityDecision EvaluateGeneric(
            EmailCaseResolutionSnapshot snapshot,
            EmailCaseResolutionAnalysis analysis,
            bool resolverFailed)
        {
            if (resolverFailed)
                return EmailFilingAuthorityDecision.Blocked(EmailFilingAuthorityKinds.BlockedResolverError);
            if (snapshot.TikResults.Count(result => result.TikCounters.Count > 0) > 1)
                return EmailFilingAuthorityDecision.Blocked(EmailFilingAuthorityKinds.BlockedMultiTik);
            if (HasConflict(analysis))
                return EmailFilingAuthorityDecision.Blocked(EmailFilingAuthorityKinds.BlockedConflict);
            if (analysis.PrimaryResults.Any(result =>
                    result.Status is PrimaryNarrowingStatus.NotFound or PrimaryNarrowingStatus.Ambiguous))
                return EmailFilingAuthorityDecision.Blocked(EmailFilingAuthorityKinds.BlockedAmbiguous);
            if (WasInsuredNameDecisive(analysis))
                return EmailFilingAuthorityDecision.Blocked(EmailFilingAuthorityKinds.BlockedInsuredDecisive);

            var finalCandidates = analysis.PrimaryResults
                .Where(result => result.Status == PrimaryNarrowingStatus.Unique)
                .SelectMany(result => result.RemainingTikCounters)
                .Distinct()
                .ToArray();
            if (finalCandidates.Length > 1)
                return EmailFilingAuthorityDecision.Blocked(EmailFilingAuthorityKinds.BlockedMultipleCandidates);

            var safeResults = analysis.PrimaryResults
                .Where(result =>
                    result.PrimaryResult.Status == EvidenceResolutionStatus.Unique &&
                    result.Status == PrimaryNarrowingStatus.Unique &&
                    result.PrimaryResult.EvidenceType is
                        EmailEvidenceType.InternalTikNumber or EmailEvidenceType.CourtCaseNumber)
                .ToArray();
            var safeCandidates = safeResults
                .SelectMany(result => result.RemainingTikCounters)
                .Distinct()
                .ToArray();
            if (safeCandidates.Length != 1 ||
                (finalCandidates.Length == 1 && finalCandidates[0] != safeCandidates[0]))
            {
                return EmailFilingAuthorityDecision.Blocked(
                    EmailFilingAuthorityKinds.BlockedNoSafeAuthority);
            }

            var kind = safeResults.Any(result =>
                result.PrimaryResult.EvidenceType == EmailEvidenceType.InternalTikNumber)
                ? EmailFilingAuthorityKinds.ExactTik
                : EmailFilingAuthorityKinds.ExactCourt;
            return EmailFilingAuthorityDecision.Authorized(kind, safeCandidates[0]);
        }

        public static EmailFilingAuthorityDecision EvaluateDirect(
            EmailCaseEvidence directEvidence,
            EmailCaseResolutionAnalysis analysis,
            bool resolverFailed)
        {
            if (resolverFailed)
                return EmailFilingAuthorityDecision.Blocked(EmailFilingAuthorityKinds.BlockedResolverError);
            if (HasConflict(analysis))
                return EmailFilingAuthorityDecision.Blocked(EmailFilingAuthorityKinds.BlockedConflict);
            if (WasInsuredNameDecisive(analysis))
                return EmailFilingAuthorityDecision.Blocked(EmailFilingAuthorityKinds.BlockedInsuredDecisive);
            if (directEvidence.SourceTemplate != EmailSourceTemplate.DirectInsurance ||
                directEvidence.PreferredClaimNumbers
                    .Select(value => value.NormalizedValue)
                    .Distinct(StringComparer.Ordinal)
                    .Count() != 1)
            {
                return EmailFilingAuthorityDecision.Blocked(
                    EmailFilingAuthorityKinds.BlockedNoSafeAuthority);
            }

            if (analysis.PrimaryResults.Any(result =>
                    result.PrimaryResult.EvidenceType != EmailEvidenceType.ClaimNumber &&
                    result.Status != PrimaryNarrowingStatus.Unique))
            {
                return EmailFilingAuthorityDecision.Blocked(
                    EmailFilingAuthorityKinds.BlockedAmbiguous);
            }

            var finalCandidates = analysis.PrimaryResults
                .Where(result => result.Status == PrimaryNarrowingStatus.Unique)
                .SelectMany(result => result.RemainingTikCounters)
                .Distinct()
                .ToArray();
            if (finalCandidates.Length > 1)
            {
                return EmailFilingAuthorityDecision.Blocked(
                    EmailFilingAuthorityKinds.BlockedMultipleCandidates);
            }

            var claimResults = analysis.PrimaryResults
                .Where(result => result.PrimaryResult.EvidenceType == EmailEvidenceType.ClaimNumber)
                .ToArray();
            var result = claimResults.Length == 1 ? claimResults[0] : null;
            if (result == null ||
                result.PrimaryResult.EvidenceType != EmailEvidenceType.ClaimNumber ||
                result.Status != PrimaryNarrowingStatus.Unique ||
                result.RemainingTikCounters.Count != 1)
            {
                return EmailFilingAuthorityDecision.Blocked(
                    result?.Status == PrimaryNarrowingStatus.Ambiguous
                        ? EmailFilingAuthorityKinds.BlockedAmbiguous
                        : EmailFilingAuthorityKinds.BlockedNoSafeAuthority);
            }

            if (result.PrimaryResult.Status == EvidenceResolutionStatus.Unique)
            {
                return EmailFilingAuthorityDecision.Authorized(
                    EmailFilingAuthorityKinds.DirectUniqueClaim,
                    result.RemainingTikCounters[0]);
            }

            if (result.PrimaryResult.Status == EvidenceResolutionStatus.Ambiguous &&
                result.Classifications.Contains(
                    "NARROWED_TO_UNIQUE_BY_VEHICLE",
                    StringComparer.Ordinal))
            {
                return EmailFilingAuthorityDecision.Authorized(
                    EmailFilingAuthorityKinds.DirectClaimVehicle,
                    result.RemainingTikCounters[0]);
            }

            return EmailFilingAuthorityDecision.Blocked(
                EmailFilingAuthorityKinds.BlockedNoSafeAuthority);
        }

        private static bool HasConflict(EmailCaseResolutionAnalysis analysis)
            => analysis.PrimaryResults.Any(result => result.Status == PrimaryNarrowingStatus.Conflict) ||
               analysis.ObserverClassifications.Contains("PRIMARY_CONFLICT", StringComparer.Ordinal) ||
               analysis.ObserverClassifications.Contains("SUPPORTING_EVIDENCE_CONFLICT", StringComparer.Ordinal);

        private static bool WasInsuredNameDecisive(EmailCaseResolutionAnalysis analysis)
            => analysis.PrimaryResults.Any(result =>
                result.Classifications.Contains(
                    "NARROWED_TO_UNIQUE_BY_INSURED_NAME",
                    StringComparer.Ordinal));
    }
}
