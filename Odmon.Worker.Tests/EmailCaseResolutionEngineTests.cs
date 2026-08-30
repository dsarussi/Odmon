using Odmon.Worker.Models;
using Odmon.Worker.OdcanitAccess;
using Odmon.Worker.Services;
using Xunit;

namespace Odmon.Worker.Tests
{
    public sealed class EmailCaseResolutionEngineTests
    {
        [Fact]
        public async Task AmbiguousClaimAndVehicleMatchingOneNarrowsToUnique()
        {
            var analysis = await AnalyzeAsync(
                Claim(100, 200),
                Evidence(vehicle: "2222222"),
                Matches((EmailEvidenceType.VehicleNumber, [200])));

            var result = Assert.Single(analysis.PrimaryResults);
            Assert.Equal([200], result.RemainingTikCounters);
            Assert.Equal(PrimaryNarrowingStatus.Unique, result.Status);
            Assert.Contains("NARROWED_BY_VEHICLE", analysis.ObserverClassifications);
        }

        [Fact]
        public async Task VehicleMatchingBothLeavesPrimaryAmbiguous()
        {
            var analysis = await AnalyzeAsync(
                Claim(100, 200),
                Evidence(vehicle: "2222222"),
                Matches((EmailEvidenceType.VehicleNumber, [100, 200])));

            var result = Assert.Single(analysis.PrimaryResults);
            Assert.Equal([100, 200], result.RemainingTikCounters);
            Assert.Equal(PrimaryNarrowingStatus.Ambiguous, result.Status);
            Assert.Contains("PRIMARY_STILL_AMBIGUOUS", analysis.ObserverClassifications);
            Assert.Contains("SUPPORTING_EVIDENCE_NO_EFFECT", analysis.ObserverClassifications);
        }

        [Fact]
        public async Task VehicleMatchingNeitherCreatesEmptyIntersectionConflict()
        {
            var analysis = await AnalyzeAsync(
                Claim(100, 200),
                Evidence(vehicle: "9999999"),
                Matches((EmailEvidenceType.VehicleNumber, [])));

            var result = Assert.Single(analysis.PrimaryResults);
            Assert.Empty(result.RemainingTikCounters);
            Assert.Equal(PrimaryNarrowingStatus.Conflict, result.Status);
            Assert.Contains("SUPPORTING_EVIDENCE_CONFLICT", analysis.ObserverClassifications);
        }

        [Fact]
        public async Task VehicleAloneNeverQueriesRepositoryOrCreatesPrimaryOutcome()
        {
            var repository = Matches((EmailEvidenceType.VehicleNumber, [200]));
            var engine = new EmailCaseResolutionEngine(repository);

            var analysis = await engine.AnalyzeAsync(
                Evidence(vehicle: "2222222"),
                EmailCaseResolutionSnapshot.Empty,
                CancellationToken.None);

            Assert.Empty(analysis.PrimaryResults);
            Assert.Equal(0, repository.SupportingCallCount);
            Assert.Contains("SUPPORTING_EVIDENCE_NO_EFFECT", analysis.ObserverClassifications);
        }

        [Theory]
        [InlineData(new[] { 200 }, PrimaryNarrowingStatus.Unique)]
        [InlineData(new[] { 100, 200 }, PrimaryNarrowingStatus.Ambiguous)]
        [InlineData(new int[0], PrimaryNarrowingStatus.Conflict)]
        public async Task EventDateNarrowingHandlesOneManyAndZero(
            int[] matches,
            PrimaryNarrowingStatus expectedStatus)
        {
            var analysis = await AnalyzeAsync(
                Claim(100, 200),
                Evidence(eventDate: "2026-08-30"),
                Matches((EmailEvidenceType.EventDate, matches)));

            Assert.Equal(expectedStatus, Assert.Single(analysis.PrimaryResults).Status);
        }

        [Fact]
        public async Task CombinedDateAndVehicleDeterministicallyNarrowToOne()
        {
            var analysis = await AnalyzeAsync(
                Claim(100, 200, 300),
                Evidence(vehicle: "2222222", eventDate: "2026-08-30"),
                Matches(
                    (EmailEvidenceType.VehicleNumber, [200]),
                    (EmailEvidenceType.EventDate, [200, 300])));

            Assert.Equal([200], Assert.Single(analysis.PrimaryResults).RemainingTikCounters);
            Assert.Contains("NARROWED_BY_VEHICLE", analysis.ObserverClassifications);
            Assert.Contains("NARROWED_BY_EVENT_DATE", analysis.ObserverClassifications);
        }

        [Fact]
        public async Task ContradictoryDateAndVehicleProduceEmptyIntersection()
        {
            var analysis = await AnalyzeAsync(
                Claim(100, 200),
                Evidence(vehicle: "2222222", eventDate: "2026-08-30"),
                Matches(
                    (EmailEvidenceType.VehicleNumber, [200]),
                    (EmailEvidenceType.EventDate, [100])));

            var result = Assert.Single(analysis.PrimaryResults);
            Assert.Empty(result.RemainingTikCounters);
            Assert.Equal(PrimaryNarrowingStatus.Conflict, result.Status);
            Assert.Contains("SUPPORTING_EVIDENCE_CONFLICT", analysis.ObserverClassifications);
        }

        [Fact]
        public async Task UniqueClaimWithAgreeingSupportRemainsUnique()
        {
            var analysis = await AnalyzeAsync(
                Claim(200),
                Evidence(vehicle: "2222222"),
                Matches((EmailEvidenceType.VehicleNumber, [200])));

            var result = Assert.Single(analysis.PrimaryResults);
            Assert.Equal([200], result.RemainingTikCounters);
            Assert.Equal(PrimaryNarrowingStatus.Unique, result.Status);
            Assert.Contains("SUPPORTING_EVIDENCE_NO_EFFECT", analysis.ObserverClassifications);
        }

        [Fact]
        public async Task UniqueClaimWithContradictorySupportBecomesConflict()
        {
            var analysis = await AnalyzeAsync(
                Claim(200),
                Evidence(vehicle: "1111111"),
                Matches((EmailEvidenceType.VehicleNumber, [100])));

            var result = Assert.Single(analysis.PrimaryResults);
            Assert.Empty(result.RemainingTikCounters);
            Assert.Equal(PrimaryNarrowingStatus.Conflict, result.Status);
        }

        [Fact]
        public async Task AmbiguousCourtCanBeNarrowedBySupportingEvidence()
        {
            var snapshot = new EmailCaseResolutionSnapshot(
                [],
                [],
                [new(EmailEvidenceType.CourtCaseNumber, "12345-01-26", [100, 200])]);

            var analysis = await AnalyzeAsync(
                snapshot,
                Evidence(eventDate: "2026-08-30"),
                Matches((EmailEvidenceType.EventDate, [200])));

            Assert.Equal([200], Assert.Single(analysis.PrimaryResults).RemainingTikCounters);
        }

        [Fact]
        public async Task MultiplePrimariesCanAgreeAfterNarrowing()
        {
            var snapshot = new EmailCaseResolutionSnapshot(
                [],
                [new(EmailEvidenceType.ClaimNumber, "CLAIM", [100, 200])],
                [new(EmailEvidenceType.CourtCaseNumber, "COURT", [200, 300])]);

            var analysis = await AnalyzeAsync(
                snapshot,
                Evidence(vehicle: "2222222"),
                Matches((EmailEvidenceType.VehicleNumber, [200])));

            Assert.All(
                analysis.PrimaryResults,
                result => Assert.Equal([200], result.RemainingTikCounters));
            Assert.Contains("PRIMARY_AGREEMENT", analysis.ObserverClassifications);
        }

        [Fact]
        public async Task MultiplePrimariesCanConflictAfterNarrowing()
        {
            var snapshot = new EmailCaseResolutionSnapshot(
                [],
                [new(EmailEvidenceType.ClaimNumber, "CLAIM", [100, 200])],
                [new(EmailEvidenceType.CourtCaseNumber, "COURT", [200, 300])]);

            var analysis = await AnalyzeAsync(
                snapshot,
                Evidence(vehicle: "9999999"),
                Matches((EmailEvidenceType.VehicleNumber, [100, 300])));

            Assert.Equal([100], analysis.PrimaryResults[0].RemainingTikCounters);
            Assert.Equal([300], analysis.PrimaryResults[1].RemainingTikCounters);
            Assert.Contains("PRIMARY_CONFLICT", analysis.ObserverClassifications);
        }

        [Theory]
        [InlineData(EmailEvidenceType.InsuredName, "NARROWED_BY_INSURED_NAME")]
        [InlineData(EmailEvidenceType.DriverPhone, "NARROWED_BY_DRIVER_PHONE")]
        [InlineData(EmailEvidenceType.ClientHint, "NARROWED_BY_CLIENT")]
        public async Task OtherSupportingTypesUseDeterministicIntersection(
            EmailEvidenceType supportType,
            string expectedClassification)
        {
            var evidence = supportType switch
            {
                EmailEvidenceType.InsuredName => Evidence(insuredName: "Synthetic Insured"),
                EmailEvidenceType.DriverPhone => Evidence(driverPhone: "0501234567"),
                EmailEvidenceType.ClientHint => Evidence(clientHint: "CLIENT-1"),
                _ => throw new ArgumentOutOfRangeException(nameof(supportType))
            };
            var analysis = await AnalyzeAsync(
                Claim(100, 200),
                evidence,
                Matches((supportType, [200])));

            Assert.Equal([200], Assert.Single(analysis.PrimaryResults).RemainingTikCounters);
            Assert.Contains(expectedClassification, analysis.ObserverClassifications);
        }

        [Fact]
        public async Task MultipleValuesOfOneSupportingTypeAreNotAppliedWithoutContextGrouping()
        {
            var repository = Matches((EmailEvidenceType.VehicleNumber, [200]));
            var engine = new EmailCaseResolutionEngine(repository);
            var evidence = Evidence() with
            {
                VehicleNumbers =
                [
                    Support(EmailEvidenceType.VehicleNumber, "1111111"),
                    Support(EmailEvidenceType.VehicleNumber, "2222222")
                ]
            };

            var analysis = await engine.AnalyzeAsync(
                evidence,
                Claim(100, 200),
                CancellationToken.None);

            Assert.Equal([100, 200], Assert.Single(analysis.PrimaryResults).RemainingTikCounters);
            Assert.Equal(0, repository.SupportingCallCount);
            Assert.Contains("SUPPORTING_EVIDENCE_NO_EFFECT", analysis.ObserverClassifications);
        }

        [Fact]
        public async Task MultipleValuesOfOnePrimaryTypeAreNotGloballyNarrowedWithoutContextGrouping()
        {
            var repository = Matches((EmailEvidenceType.VehicleNumber, [200]));
            var engine = new EmailCaseResolutionEngine(repository);
            var snapshot = new EmailCaseResolutionSnapshot(
                [],
                [
                    new(EmailEvidenceType.ClaimNumber, "CLAIM-1", [100, 200]),
                    new(EmailEvidenceType.ClaimNumber, "CLAIM-2", [200, 300])
                ],
                []);

            var analysis = await engine.AnalyzeAsync(
                Evidence(vehicle: "2222222"),
                snapshot,
                CancellationToken.None);

            Assert.Equal(0, repository.SupportingCallCount);
            Assert.Equal([100, 200], analysis.PrimaryResults[0].RemainingTikCounters);
            Assert.Equal([200, 300], analysis.PrimaryResults[1].RemainingTikCounters);
            Assert.Contains("SUPPORTING_EVIDENCE_NO_EFFECT", analysis.ObserverClassifications);
        }

        private static Task<EmailCaseResolutionAnalysis> AnalyzeAsync(
            EmailCaseResolutionSnapshot snapshot,
            EmailCaseEvidence evidence,
            FakeResolutionRepository repository)
            => new EmailCaseResolutionEngine(repository).AnalyzeAsync(
                evidence,
                snapshot,
                CancellationToken.None);

        private static EmailCaseResolutionSnapshot Claim(params int[] counters)
            => new(
                [],
                [new(EmailEvidenceType.ClaimNumber, "CLAIM", counters)],
                []);

        private static EmailCaseEvidence Evidence(
            string? vehicle = null,
            string? eventDate = null,
            string? clientHint = null,
            string? insuredName = null,
            string? driverPhone = null)
            => new(
                [],
                [],
                [],
                vehicle == null ? [] : [Support(EmailEvidenceType.VehicleNumber, vehicle)],
                eventDate == null ? [] : [Support(EmailEvidenceType.EventDate, eventDate)],
                clientHint == null ? [] : [Support(EmailEvidenceType.ClientHint, clientHint)],
                insuredName == null ? [] : [Support(EmailEvidenceType.InsuredName, insuredName)],
                driverPhone == null ? [] : [Support(EmailEvidenceType.DriverPhone, driverPhone)]);

        private static EmailEvidenceValue Support(
            EmailEvidenceType type,
            string value)
            => new(
                type,
                value,
                EmailEvidenceSource.Body,
                EmailEvidenceExtractionKind.ExplicitLabel);

        private static FakeResolutionRepository Matches(
            params (EmailEvidenceType Type, int[] Counters)[] matches)
            => new(matches.ToDictionary(match => match.Type, match => match.Counters.ToHashSet()));

        private sealed class FakeResolutionRepository(
            IReadOnlyDictionary<EmailEvidenceType, HashSet<int>> matches)
            : IEmailCaseResolutionRepository
        {
            public int SupportingCallCount { get; private set; }

            public Task<IReadOnlyList<EvidenceResolutionResult>> ResolveInternalTikNumbersAsync(
                IEnumerable<string> normalizedValues,
                CancellationToken cancellationToken)
                => EmptyPrimary();

            public Task<IReadOnlyList<EvidenceResolutionResult>> ResolveClaimNumbersAsync(
                IEnumerable<string> normalizedValues,
                CancellationToken cancellationToken)
                => EmptyPrimary();

            public Task<IReadOnlyList<EvidenceResolutionResult>> ResolveCourtCaseNumbersAsync(
                IEnumerable<string> normalizedValues,
                CancellationToken cancellationToken)
                => EmptyPrimary();

            public Task<IReadOnlySet<int>> FilterCandidatesByVehicleAsync(
                IEnumerable<int> candidateTikCounters,
                IEnumerable<string> normalizedValues,
                CancellationToken cancellationToken)
                => Filter(EmailEvidenceType.VehicleNumber, candidateTikCounters);

            public Task<IReadOnlySet<int>> FilterCandidatesByEventDateAsync(
                IEnumerable<int> candidateTikCounters,
                IEnumerable<string> normalizedValues,
                CancellationToken cancellationToken)
                => Filter(EmailEvidenceType.EventDate, candidateTikCounters);

            public Task<IReadOnlySet<int>> FilterCandidatesByClientAsync(
                IEnumerable<int> candidateTikCounters,
                IEnumerable<string> normalizedValues,
                CancellationToken cancellationToken)
                => Filter(EmailEvidenceType.ClientHint, candidateTikCounters);

            public Task<IReadOnlySet<int>> FilterCandidatesByInsuredNameAsync(
                IEnumerable<int> candidateTikCounters,
                IEnumerable<string> normalizedValues,
                CancellationToken cancellationToken)
                => Filter(EmailEvidenceType.InsuredName, candidateTikCounters);

            public Task<IReadOnlySet<int>> FilterCandidatesByDriverPhoneAsync(
                IEnumerable<int> candidateTikCounters,
                IEnumerable<string> normalizedValues,
                CancellationToken cancellationToken)
                => Filter(EmailEvidenceType.DriverPhone, candidateTikCounters);

            private Task<IReadOnlySet<int>> Filter(
                EmailEvidenceType type,
                IEnumerable<int> candidates)
            {
                SupportingCallCount++;
                var candidateSet = candidates.ToHashSet();
                var result = matches.TryGetValue(type, out var configured)
                    ? configured.Where(candidateSet.Contains).ToHashSet()
                    : [];
                return Task.FromResult<IReadOnlySet<int>>(result);
            }

            private static Task<IReadOnlyList<EvidenceResolutionResult>> EmptyPrimary()
                => Task.FromResult<IReadOnlyList<EvidenceResolutionResult>>([]);
        }
    }
}
