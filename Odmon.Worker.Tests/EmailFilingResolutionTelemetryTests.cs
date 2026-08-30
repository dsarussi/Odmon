using Microsoft.EntityFrameworkCore;
using Odmon.Worker.Data;
using Odmon.Worker.Models;
using Odmon.Worker.Services;
using Xunit;

namespace Odmon.Worker.Tests
{
    public sealed class EmailFilingResolutionTelemetryTests
    {
        private static readonly DateTime CreatedUtc =
            new(2026, 8, 30, 12, 0, 0, DateTimeKind.Utc);

        [Theory]
        [MemberData(nameof(AgreementCases))]
        public void AgreementClassificationUsesSetRelations(
            int[] existing,
            int[] phantom,
            string expected)
        {
            var actual = EmailFilingResolutionTelemetry.ClassifyAgreement(
                existing.ToHashSet(),
                phantom.ToHashSet());

            Assert.Equal(expected, actual);
        }

        public static TheoryData<int[], int[], string> AgreementCases => new()
        {
            { [1], [1], EmailFilingResolutionAgreements.ExactAgreement },
            { [1], [1, 2], EmailFilingResolutionAgreements.PhantomSuperset },
            { [1, 2], [1], EmailFilingResolutionAgreements.PhantomSubset },
            { [1, 2], [2, 3], EmailFilingResolutionAgreements.PartialOverlap },
            { [1], [2], EmailFilingResolutionAgreements.Disjoint },
            { [1], [], EmailFilingResolutionAgreements.PhantomUnresolved },
            { [], [1], EmailFilingResolutionAgreements.NoExistingTikAuthority }
        };

        [Fact]
        public void AmbiguousClaimNarrowedToUniqueCreatesObserverTargetOnly()
        {
            var primary = Result(EmailEvidenceType.ClaimNumber, [10, 20]);
            var snapshot = new EmailCaseResolutionSnapshot([], [primary], []);
            var analysis = new EmailCaseResolutionAnalysis(
                snapshot,
                [new(primary, [20], PrimaryNarrowingStatus.Unique, ["NARROWED_BY_VEHICLE"])],
                ["CLAIM_AMBIGUOUS", "NARROWED_BY_VEHICLE"]);

            var run = CreateRun(Evidence(claim: true, vehicle: true), snapshot, analysis, []);

            Assert.Equal(EmailFilingResolutionClasses.NarrowedToUnique, run.FinalResolutionClass);
            Assert.True(run.NarrowingApplied);
            Assert.Equal(1, run.PhantomTargetCount);
            var target = Assert.Single(run.Targets);
            Assert.Equal(20, target.TikCounter);
            Assert.Equal(EmailFilingResolutionTargetKinds.PhantomWouldFile, target.TargetKind);
        }

        [Fact]
        public void AmbiguousResultRemainsUnresolved()
        {
            var primary = Result(EmailEvidenceType.ClaimNumber, [10, 20]);
            var snapshot = new EmailCaseResolutionSnapshot([], [primary], []);
            var analysis = new EmailCaseResolutionAnalysis(
                snapshot,
                [new(primary, [10, 20], PrimaryNarrowingStatus.Ambiguous, [])],
                ["CLAIM_AMBIGUOUS"]);

            var run = CreateRun(Evidence(claim: true), snapshot, analysis, []);

            Assert.Equal(EmailFilingResolutionClasses.PrimaryAmbiguous, run.FinalResolutionClass);
            Assert.Empty(run.Targets);
        }

        [Fact]
        public void SupportingConflictProducesNoObserverTarget()
        {
            var primary = Result(EmailEvidenceType.CourtCaseNumber, [10, 20]);
            var snapshot = new EmailCaseResolutionSnapshot([], [], [primary]);
            var analysis = new EmailCaseResolutionAnalysis(
                snapshot,
                [new(primary, [], PrimaryNarrowingStatus.Conflict, ["SUPPORTING_EVIDENCE_CONFLICT"])],
                ["SUPPORTING_EVIDENCE_CONFLICT"]);

            var run = CreateRun(Evidence(court: true, phone: true), snapshot, analysis, []);

            Assert.Equal(EmailFilingResolutionClasses.SupportingConflict, run.FinalResolutionClass);
            Assert.Equal(0, run.PhantomTargetCount);
        }

        [Fact]
        public void MultipleExplicitTikResultsProduceMultipleObserverTargets()
        {
            var first = Result(EmailEvidenceType.InternalTikNumber, [10], "1/1");
            var second = Result(EmailEvidenceType.InternalTikNumber, [20], "2/2");
            var snapshot = new EmailCaseResolutionSnapshot([first, second], [], []);
            var analysis = EmailCaseResolutionAnalysis.WithoutSupportingNarrowing(snapshot);

            var run = CreateRun(Evidence(tikCount: 2), snapshot, analysis, [10, 20]);

            Assert.Equal(EmailFilingResolutionClasses.MultiTik, run.FinalResolutionClass);
            Assert.Equal(2, run.PhantomTargetCount);
            Assert.Equal(2, run.ExistingAuthorityTargetCount);
            Assert.Equal(EmailFilingResolutionAgreements.ExactAgreement, run.AgreementWithExistingAuthority);
            Assert.Equal(4, run.Targets.Count);
        }

        [Fact]
        public void ObserverErrorStoresCategoryOnlyAndNoObserverTarget()
        {
            var errorCategory = nameof(InvalidOperationException);
            var run = EmailFilingResolutionTelemetry.CreateRun(
                null,
                EmailCaseResolutionSnapshot.Empty,
                EmailCaseResolutionAnalysis.Empty,
                [10],
                new(1, 2, 3, 6),
                errorCategory,
                CreatedUtc);

            Assert.Equal(EmailFilingResolutionClasses.ObserverError, run.FinalResolutionClass);
            Assert.Equal(errorCategory, run.ObserverErrorCategory);
            Assert.Equal(0, run.PhantomTargetCount);
            Assert.Equal(EmailFilingResolutionAgreements.PhantomUnresolved, run.AgreementWithExistingAuthority);
            Assert.DoesNotContain(run.Targets, target =>
                target.TargetKind == EmailFilingResolutionTargetKinds.PhantomWouldFile);
        }

        [Fact]
        public void TelemetryModelHasPrivacySafeColumnsAndRequiredUniqueIndexes()
        {
            using var db = CreateDb();
            var runType = db.Model.FindEntityType(typeof(EmailFilingResolutionRun));
            var targetType = db.Model.FindEntityType(typeof(EmailFilingResolutionTarget));
            Assert.NotNull(runType);
            Assert.NotNull(targetType);

            var propertyNames = runType!.GetProperties()
                .Concat(targetType!.GetProperties())
                .Select(property => property.Name)
                .ToArray();
            var forbiddenFragments = new[]
            {
                "ClaimNumber", "CourtCaseNumber", "VehicleNumber", "EventDate",
                "Client", "Insured", "Phone", "Subject", "Body", "Attachment",
                "Sender", "Recipient", "Graph", "MessageId", "Path"
            };
            Assert.DoesNotContain(propertyNames, name => forbiddenFragments.Any(fragment =>
                name.Contains(fragment, StringComparison.OrdinalIgnoreCase)));
            Assert.Contains(runType.GetIndexes(), index => index.IsUnique &&
                index.Properties.Select(property => property.Name)
                    .SequenceEqual([nameof(EmailFilingResolutionRun.EmailFilingDiagnosticId)]));
            Assert.Contains(targetType.GetIndexes(), index => index.IsUnique &&
                index.Properties.Select(property => property.Name).SequenceEqual(
                    [nameof(EmailFilingResolutionTarget.ResolutionRunId),
                     nameof(EmailFilingResolutionTarget.TikCounter),
                     nameof(EmailFilingResolutionTarget.TargetKind)]));
        }

        private static EmailFilingResolutionRun CreateRun(
            EmailCaseEvidence evidence,
            EmailCaseResolutionSnapshot snapshot,
            EmailCaseResolutionAnalysis analysis,
            IEnumerable<int> existing)
            => EmailFilingResolutionTelemetry.CreateRun(
                evidence,
                snapshot,
                analysis,
                existing,
                new(1, 2, 3, 6),
                null,
                CreatedUtc);

        private static EvidenceResolutionResult Result(
            EmailEvidenceType type,
            int[] counters,
            string value = "SYNTHETIC")
            => new(type, value, counters);

        private static EmailCaseEvidence Evidence(
            bool claim = false,
            bool court = false,
            bool vehicle = false,
            bool phone = false,
            int tikCount = 0)
            => new(
                Enumerable.Range(1, tikCount)
                    .Select(index => Value(EmailEvidenceType.InternalTikNumber, $"{index}/{index}"))
                    .ToArray(),
                claim ? [Value(EmailEvidenceType.ClaimNumber)] : [],
                court ? [Value(EmailEvidenceType.CourtCaseNumber)] : [],
                vehicle ? [Value(EmailEvidenceType.VehicleNumber)] : [],
                [],
                [],
                [],
                phone ? [Value(EmailEvidenceType.DriverPhone)] : []);

        private static EmailEvidenceValue Value(
            EmailEvidenceType type,
            string value = "SYNTHETIC")
            => new(
                type,
                value,
                EmailEvidenceSource.Subject,
                EmailEvidenceExtractionKind.ExplicitLabel);

        private static IntegrationDbContext CreateDb()
        {
            var options = new DbContextOptionsBuilder<IntegrationDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options;
            return new IntegrationDbContext(options);
        }
    }
}
