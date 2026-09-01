using Odmon.Worker.Models;
using Odmon.Worker.Services;
using Xunit;

namespace Odmon.Worker.Tests
{
    public sealed class EmailFilingRealWriteGateTests
    {
        [Fact]
        public void SupportingConflictIsBlocked()
        {
            var primary = Result(EmailEvidenceType.InternalTikNumber, EvidenceResolutionStatus.Unique, [10]);
            var analysis = Analysis(
                primary,
                PrimaryNarrowingStatus.Conflict,
                [],
                ["SUPPORTING_EVIDENCE_CONFLICT"]);

            var decision = EmailFilingRealWriteGate.EvaluateGeneric(
                Snapshot(tik: [primary]),
                analysis,
                resolverFailed: false);

            Assert.False(decision.IsAuthorized);
            Assert.Equal(EmailFilingAuthorityKinds.BlockedConflict, decision.AuthorityKind);
        }

        [Fact]
        public void PrimaryConflictIsBlocked()
        {
            var tik = Result(EmailEvidenceType.InternalTikNumber, EvidenceResolutionStatus.Unique, [10]);
            var claim = Result(EmailEvidenceType.ClaimNumber, EvidenceResolutionStatus.Unique, [20]);
            var analysis = new EmailCaseResolutionAnalysis(
                Snapshot(tik: [tik], claim: [claim]),
                [
                    Narrowed(tik, PrimaryNarrowingStatus.Unique, [10]),
                    Narrowed(claim, PrimaryNarrowingStatus.Unique, [20])
                ],
                ["PRIMARY_CONFLICT"]);

            var decision = EmailFilingRealWriteGate.EvaluateGeneric(
                analysis.PrimarySnapshot,
                analysis,
                resolverFailed: false);

            Assert.False(decision.IsAuthorized);
            Assert.Equal(EmailFilingAuthorityKinds.BlockedConflict, decision.AuthorityKind);
        }

        [Fact]
        public void MultipleExactTikValuesAreBlocked()
        {
            var first = Result(EmailEvidenceType.InternalTikNumber, EvidenceResolutionStatus.Unique, [10]);
            var second = Result(EmailEvidenceType.InternalTikNumber, EvidenceResolutionStatus.Unique, [20]);
            var snapshot = Snapshot(tik: [first, second]);
            var analysis = new EmailCaseResolutionAnalysis(
                snapshot,
                [
                    Narrowed(first, PrimaryNarrowingStatus.Unique, [10]),
                    Narrowed(second, PrimaryNarrowingStatus.Unique, [20])
                ],
                ["PRIMARY_CONFLICT"]);

            var decision = EmailFilingRealWriteGate.EvaluateGeneric(
                snapshot,
                analysis,
                resolverFailed: false);

            Assert.False(decision.IsAuthorized);
            Assert.Equal(EmailFilingAuthorityKinds.BlockedMultiTik, decision.AuthorityKind);
        }

        [Fact]
        public void AmbiguousPrimaryIsBlocked()
        {
            var primary = Result(EmailEvidenceType.CourtCaseNumber, EvidenceResolutionStatus.Ambiguous, [10, 20]);
            var analysis = Analysis(primary, PrimaryNarrowingStatus.Ambiguous, [10, 20]);

            var decision = EmailFilingRealWriteGate.EvaluateGeneric(
                Snapshot(court: [primary]),
                analysis,
                resolverFailed: false);

            Assert.False(decision.IsAuthorized);
            Assert.Equal(EmailFilingAuthorityKinds.BlockedAmbiguous, decision.AuthorityKind);
        }

        [Fact]
        public void InsuredNameDecisiveDirectNarrowingIsBlocked()
        {
            var primary = Result(EmailEvidenceType.ClaimNumber, EvidenceResolutionStatus.Ambiguous, [10, 20]);
            var analysis = Analysis(
                primary,
                PrimaryNarrowingStatus.Unique,
                [20],
                ["NARROWED_BY_INSURED_NAME", "NARROWED_TO_UNIQUE_BY_INSURED_NAME"]);
            var preferred = new EmailEvidenceValue(
                EmailEvidenceType.ClaimNumber,
                "SYNTHETIC-CLAIM",
                EmailEvidenceSource.Body,
                EmailEvidenceExtractionKind.ExplicitLabel);
            var evidence = new EmailCaseEvidence([], [preferred], [], [], [], [], [], [])
            {
                SourceTemplate = EmailSourceTemplate.DirectInsurance,
                PreferredClaimNumbers = [preferred]
            };

            var decision = EmailFilingRealWriteGate.EvaluateDirect(
                evidence,
                analysis,
                resolverFailed: false);

            Assert.False(decision.IsAuthorized);
            Assert.Equal(EmailFilingAuthorityKinds.BlockedInsuredDecisive, decision.AuthorityKind);
        }

        [Fact]
        public void DirectClaimConflictingWithExactCourtIsBlocked()
        {
            var claim = Result(EmailEvidenceType.ClaimNumber, EvidenceResolutionStatus.Unique, [10]);
            var court = Result(EmailEvidenceType.CourtCaseNumber, EvidenceResolutionStatus.Unique, [20]);
            var snapshot = Snapshot(claim: [claim], court: [court]);
            var analysis = new EmailCaseResolutionAnalysis(
                snapshot,
                [
                    Narrowed(claim, PrimaryNarrowingStatus.Unique, [10]),
                    Narrowed(court, PrimaryNarrowingStatus.Unique, [20])
                ],
                ["PRIMARY_CONFLICT"]);
            var preferred = new EmailEvidenceValue(
                EmailEvidenceType.ClaimNumber,
                "SYNTHETIC-CLAIM",
                EmailEvidenceSource.Body,
                EmailEvidenceExtractionKind.ExplicitLabel);
            var evidence = new EmailCaseEvidence([], [preferred], [], [], [], [], [], [])
            {
                SourceTemplate = EmailSourceTemplate.DirectInsurance,
                PreferredClaimNumbers = [preferred]
            };

            var decision = EmailFilingRealWriteGate.EvaluateDirect(
                evidence,
                analysis,
                resolverFailed: false);

            Assert.False(decision.IsAuthorized);
            Assert.Equal(EmailFilingAuthorityKinds.BlockedConflict, decision.AuthorityKind);
        }

        private static EvidenceResolutionResult Result(
            EmailEvidenceType type,
            EvidenceResolutionStatus status,
            int[] counters)
        {
            var effectiveCounters = status == EvidenceResolutionStatus.NotFound ? [] : counters;
            return new EvidenceResolutionResult(type, $"SYNTHETIC-{type}", effectiveCounters);
        }

        private static EmailCaseResolutionSnapshot Snapshot(
            EvidenceResolutionResult[]? tik = null,
            EvidenceResolutionResult[]? claim = null,
            EvidenceResolutionResult[]? court = null)
            => new(tik ?? [], claim ?? [], court ?? []);

        private static EmailCaseResolutionAnalysis Analysis(
            EvidenceResolutionResult primary,
            PrimaryNarrowingStatus status,
            int[] remaining,
            string[]? classifications = null)
        {
            var snapshot = primary.EvidenceType switch
            {
                EmailEvidenceType.InternalTikNumber => Snapshot(tik: [primary]),
                EmailEvidenceType.ClaimNumber => Snapshot(claim: [primary]),
                EmailEvidenceType.CourtCaseNumber => Snapshot(court: [primary]),
                _ => throw new ArgumentOutOfRangeException(nameof(primary))
            };
            var values = classifications ?? [];
            return new EmailCaseResolutionAnalysis(
                snapshot,
                [Narrowed(primary, status, remaining, values)],
                values);
        }

        private static PrimaryNarrowingResult Narrowed(
            EvidenceResolutionResult primary,
            PrimaryNarrowingStatus status,
            int[] remaining,
            string[]? classifications = null)
            => new(primary, remaining, status, classifications ?? []);
    }
}
