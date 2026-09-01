using Microsoft.EntityFrameworkCore;
using Odmon.Worker.Models;
using Odmon.Worker.OdcanitAccess;
using Odmon.Worker.Services;
using Xunit;

namespace Odmon.Worker.Tests
{
    public sealed class EmailCaseResolutionRepositoryTests
    {
        [Fact]
        public void ExportSidesViewModelMatchesConfirmedProductionSchemaExactly()
        {
            using var db = CreateModelDb();
            var entity = db.Model.FindEntityType(typeof(OdcanitSide));
            Assert.NotNull(entity);

            Assert.Equal("vwExportToOuterSystems_vwSides", entity!.GetViewName());
            Assert.Equal("dbo", entity.GetViewSchema());
            Assert.Equal(
                new[]
                {
                    "FullAddress", "FullName", "ID", "SideTypeCode", "SideTypeName",
                    "TikCounter", "TikNumber", "tsCreateDate", "tsModifyDate"
                },
                entity.GetProperties().Select(property => property.Name).OrderBy(name => name));
            Assert.DoesNotContain(entity.GetProperties(), property =>
                property.Name == "SideDataCounter");
        }

        [Fact]
        public void EmailFilingSideDataLinkUsesSeparateDboSidesProjection()
        {
            using var db = CreateModelDb();
            var entity = db.Model.FindEntityType(typeof(OdcanitSideDataLink));
            Assert.NotNull(entity);

            Assert.Equal("SIDES", entity!.GetTableName());
            Assert.Equal("dbo", entity.GetSchema());
            Assert.Equal(
                new[] { "SideDataCounter", "TikCounter" },
                entity.GetProperties().Select(property => property.Name).OrderBy(name => name));
            Assert.NotEqual(
                db.Model.FindEntityType(typeof(OdcanitSide))!.GetViewName(),
                entity.GetTableName());
        }

        [Fact]
        public async Task InternalTik_NoMatch_ReturnsNotFound()
        {
            await using var fixture = CreateFixture(Row(1, visualId: "9/1984"));

            var result = Assert.Single(await fixture.Repository.ResolveInternalTikNumbersAsync(
                ["7/777"],
                CancellationToken.None));

            AssertResolution(result, EmailEvidenceType.InternalTikNumber, "7/777");
        }

        [Fact]
        public async Task InternalTik_OneCounter_ReturnsUnique()
        {
            await using var fixture = CreateFixture(Row(40514, visualId: "9/1984"));

            var result = Assert.Single(await fixture.Repository.ResolveInternalTikNumbersAsync(
                ["9/1984"],
                CancellationToken.None));

            AssertResolution(
                result,
                EmailEvidenceType.InternalTikNumber,
                "9/1984",
                40514);
        }

        [Fact]
        public async Task InternalTik_DottedVisualIdIsResolvedAsCompleteExactValue()
        {
            await using var fixture = CreateFixture(
                Row(7076, visualId: "94/1.7076"),
                Row(94, visualId: "94/1"));

            var result = Assert.Single(await fixture.Repository.ResolveInternalTikNumbersAsync(
                ["94/1.7076"],
                CancellationToken.None));

            AssertResolution(
                result,
                EmailEvidenceType.InternalTikNumber,
                "94/1.7076",
                7076);
        }

        [Fact]
        public async Task InternalTik_DuplicateRowsForSameCounter_RemainsUnique()
        {
            await using var fixture = CreateFixture(
                Row(40514, visualId: "9/1984", marker: "a"),
                Row(40514, visualId: "9/1984", marker: "b"));

            var result = Assert.Single(await fixture.Repository.ResolveInternalTikNumbersAsync(
                ["9/1984"],
                CancellationToken.None));

            AssertResolution(
                result,
                EmailEvidenceType.InternalTikNumber,
                "9/1984",
                40514);
        }

        [Fact]
        public async Task InternalTik_MultipleDistinctCounters_ReturnsAmbiguousSet()
        {
            await using var fixture = CreateFixture(
                Row(100, visualId: "9/1984"),
                Row(200, visualId: "9/1984"));

            var result = Assert.Single(await fixture.Repository.ResolveInternalTikNumbersAsync(
                ["9/1984"],
                CancellationToken.None));

            AssertResolution(
                result,
                EmailEvidenceType.InternalTikNumber,
                "9/1984",
                100,
                200);
        }

        [Fact]
        public async Task InternalTik_StatusIsEquivalentToCurrentV1Classification()
        {
            var rows = new[]
            {
                Row(10, visualId: "1/1", marker: "a"),
                Row(10, visualId: "1/1", marker: "b"),
                Row(20, visualId: "2/2"),
                Row(30, visualId: "2/2")
            };
            await using var fixture = CreateFixture(rows);

            var results = await fixture.Repository.ResolveInternalTikNumbersAsync(
                ["1/1", "2/2", "3/3"],
                CancellationToken.None);
            var current = SqlOdcanitReader.ClassifyTikNumberMatches(
                rows.Select(row => (row.VisualId!, row.TikCounter)));

            Assert.Equal(EvidenceResolutionStatus.Unique, results[0].Status);
            Assert.True(current["1/1"].IsResolved);
            Assert.Equal(EvidenceResolutionStatus.Ambiguous, results[1].Status);
            Assert.True(current["2/2"].IsAmbiguous);
            Assert.Equal(EvidenceResolutionStatus.NotFound, results[2].Status);
            Assert.DoesNotContain("3/3", current);
        }

        [Fact]
        public async Task ClaimNumber_NoMatch_ReturnsNotFound()
        {
            await using var fixture = CreateFixture(Row(10, additional: "CLAIM-1"));

            var result = Assert.Single(await fixture.Repository.ResolveClaimNumbersAsync(
                ["CLAIM-2"],
                CancellationToken.None));

            AssertResolution(result, EmailEvidenceType.ClaimNumber, "CLAIM-2");
        }

        [Fact]
        public async Task ClaimNumber_OneCounter_ReturnsUnique()
        {
            await using var fixture = CreateFixture(Row(10, additional: "CLAIM-1"));

            var result = Assert.Single(await fixture.Repository.ResolveClaimNumbersAsync(
                ["CLAIM-1"],
                CancellationToken.None));

            AssertResolution(result, EmailEvidenceType.ClaimNumber, "CLAIM-1", 10);
        }

        [Fact]
        public async Task ClaimNumber_DuplicateRowsForSameCounter_RemainsUnique()
        {
            await using var fixture = CreateFixture(
                Row(10, additional: "CLAIM-1", marker: "a"),
                Row(10, additional: "CLAIM-1", marker: "b"));

            var result = Assert.Single(await fixture.Repository.ResolveClaimNumbersAsync(
                ["CLAIM-1"],
                CancellationToken.None));

            AssertResolution(result, EmailEvidenceType.ClaimNumber, "CLAIM-1", 10);
        }

        [Fact]
        public async Task ClaimNumber_MultipleDistinctCounters_ReturnsAmbiguousSet()
        {
            await using var fixture = CreateFixture(
                Row(10, additional: "CLAIM-1"),
                Row(20, additional: "CLAIM-1"),
                Row(30, additional: "CLAIM-1"));

            var result = Assert.Single(await fixture.Repository.ResolveClaimNumbersAsync(
                ["CLAIM-1"],
                CancellationToken.None));

            AssertResolution(result, EmailEvidenceType.ClaimNumber, "CLAIM-1", 10, 20, 30);
        }

        [Fact]
        public async Task ClaimNumber_UsesExactNormalizedMatchOnly()
        {
            await using var fixture = CreateFixture(
                Row(10, additional: "CLAIM-10"),
                Row(20, additional: "prefix CLAIM-1"));

            var result = Assert.Single(await fixture.Repository.ResolveClaimNumbersAsync(
                [" CLAIM-1 "],
                CancellationToken.None));

            AssertResolution(result, EmailEvidenceType.ClaimNumber, "CLAIM-1");
        }

        [Fact]
        public async Task CourtNumber_NoMatch_ReturnsNotFound()
        {
            await using var fixture = CreateFixture(Row(10, court: "12345-01-26"));

            var result = Assert.Single(await fixture.Repository.ResolveCourtCaseNumbersAsync(
                ["54321-01-26"],
                CancellationToken.None));

            AssertResolution(result, EmailEvidenceType.CourtCaseNumber, "54321-01-26");
        }

        [Fact]
        public async Task CourtNumber_OneCounter_ReturnsUnique()
        {
            await using var fixture = CreateFixture(Row(10, court: "12345-01-26"));

            var result = Assert.Single(await fixture.Repository.ResolveCourtCaseNumbersAsync(
                ["12345-01-26"],
                CancellationToken.None));

            AssertResolution(result, EmailEvidenceType.CourtCaseNumber, "12345-01-26", 10);
        }

        [Fact]
        public async Task CourtNumber_DuplicateRowsForSameCounter_RemainsUnique()
        {
            await using var fixture = CreateFixture(
                Row(10, court: "12345-01-26", marker: "a"),
                Row(10, court: "12345-01-26", marker: "b"));

            var result = Assert.Single(await fixture.Repository.ResolveCourtCaseNumbersAsync(
                ["12345-01-26"],
                CancellationToken.None));

            AssertResolution(result, EmailEvidenceType.CourtCaseNumber, "12345-01-26", 10);
        }

        [Fact]
        public async Task CourtNumber_MultipleDistinctCounters_ReturnsAmbiguousSet()
        {
            await using var fixture = CreateFixture(
                Row(10, court: "12345-01-26"),
                Row(20, court: "12345-01-26"));

            var result = Assert.Single(await fixture.Repository.ResolveCourtCaseNumbersAsync(
                ["12345-01-26"],
                CancellationToken.None));

            AssertResolution(result, EmailEvidenceType.CourtCaseNumber, "12345-01-26", 10, 20);
        }

        [Fact]
        public async Task CourtNumber_DirtyContainingValueIsNotAnExactMatch()
        {
            await using var fixture = CreateFixture(
                Row(10, court: "dirty 12345-01-26 text"),
                Row(20, court: "12345-01-260"));

            var result = Assert.Single(await fixture.Repository.ResolveCourtCaseNumbersAsync(
                ["12345-01-26"],
                CancellationToken.None));

            AssertResolution(result, EmailEvidenceType.CourtCaseNumber, "12345-01-26");
        }

        [Fact]
        public async Task VehicleFilterIsNormalizedExactAndCandidateBounded()
        {
            await using var fixture = CreateSupportingFixture(
                userData:
                [
                    UserData(100, "מספר רישוי", "11-111-11"),
                    UserData(200, "מספר רישוי.", "22 222 22"),
                    UserData(999, "מספר רישוי", "22-222-22")
                ]);

            var matches = await fixture.Repository.FilterCandidatesByVehicleAsync(
                [100, 200],
                ["22-222-22"],
                CancellationToken.None);

            Assert.Equal([100, 200], matches.ComparableTikCounters.OrderBy(value => value));
            Assert.Equal([200], matches.MatchingTikCounters.OrderBy(value => value));
        }

        [Theory]
        [InlineData("30/08/2026")]
        [InlineData("2026-08-30")]
        public async Task EventDateFilterNormalizesDateAndRemainsCandidateBounded(string input)
        {
            await using var fixture = CreateSupportingFixture(
                userData:
                [
                    UserData(100, "תאריך אירוע", date: new DateTime(2026, 8, 30)),
                    UserData(999, "תאריך אירוע", date: new DateTime(2026, 8, 30))
                ]);

            var matches = await fixture.Repository.FilterCandidatesByEventDateAsync(
                [100],
                [input],
                CancellationToken.None);

            Assert.Equal([100], matches.ComparableTikCounters);
            Assert.Equal([100], matches.MatchingTikCounters);
        }

        [Fact]
        public async Task NullEventDateIsUnknownRatherThanComparableMismatch()
        {
            await using var fixture = CreateSupportingFixture(
                userData: [UserData(100, "תאריך אירוע")]);

            var result = await fixture.Repository.FilterCandidatesByEventDateAsync(
                [100],
                ["2026-08-30"],
                CancellationToken.None);

            Assert.Empty(result.ComparableTikCounters);
            Assert.Empty(result.MatchingTikCounters);
        }

        [Fact]
        public async Task InsuredNameFilterUsesConservativeExactMatching()
        {
            await using var fixture = CreateSupportingFixture(
                userData:
                [
                    UserData(100, "שם בעל פוליסה", "Synthetic Insured"),
                    UserData(200, "שם בעל פוליסה", "Synthetic Insured Extra")
                ]);

            var matches = await fixture.Repository.FilterCandidatesByInsuredNameAsync(
                [100, 200],
                ["Synthetic Insured"],
                CancellationToken.None);

            Assert.Equal([100, 200], matches.ComparableTikCounters.OrderBy(value => value));
            Assert.Equal([100], matches.MatchingTikCounters);
        }

        [Fact]
        public async Task DriverPhoneFilterNormalizesExactlyWithoutSuffixMatching()
        {
            await using var fixture = CreateSupportingFixture(
                userData:
                [
                    UserData(100, "סלולרי נהג", "050-123-4567"),
                    UserData(200, "Driver: phone", "150-123-4567")
                ]);

            var matches = await fixture.Repository.FilterCandidatesByDriverPhoneAsync(
                [100, 200],
                ["0501234567"],
                CancellationToken.None);

            Assert.Equal([100, 200], matches.ComparableTikCounters.OrderBy(value => value));
            Assert.Equal([100], matches.MatchingTikCounters);
        }

        [Fact]
        public async Task ClientFilterUsesVerifiedJoinAndCandidateBoundary()
        {
            await using var fixture = CreateSupportingFixture(
                sides:
                [
                    Side(100, 1),
                    Side(200, 2),
                    Side(999, 3)
                ],
                clients:
                [
                    Client(1, "CLIENT-1"),
                    Client(2, "CLIENT-2"),
                    Client(3, "CLIENT-2")
                ]);

            var matches = await fixture.Repository.FilterCandidatesByClientAsync(
                [100, 200],
                ["CLIENT-2"],
                CancellationToken.None);

            Assert.Equal([100, 200], matches.ComparableTikCounters.OrderBy(value => value));
            Assert.Equal([200], matches.MatchingTikCounters);
        }

        [Fact]
        public async Task SupportingFilterWithNoPrimaryCandidatesReturnsEmpty()
        {
            await using var fixture = CreateSupportingFixture(
                userData: [UserData(100, "מספר רישוי", "11-111-11")]);

            var matches = await fixture.Repository.FilterCandidatesByVehicleAsync(
                [],
                ["11-111-11"],
                CancellationToken.None);

            Assert.Empty(matches.ComparableTikCounters);
            Assert.Empty(matches.MatchingTikCounters);
        }

        [Fact]
        public async Task PrimaryResolverDeduplicatesCrossSourceValuesBeforeRepositoryLookup()
        {
            var repository = new CapturingRepository();
            var resolver = new EmailCasePrimaryResolver(repository);
            var evidenceValue = new EmailEvidenceValue(
                EmailEvidenceType.ClaimNumber,
                "CLAIM-1",
                EmailEvidenceSource.Subject,
                EmailEvidenceExtractionKind.ExplicitLabel);
            var bodyValue = evidenceValue with { Source = EmailEvidenceSource.Body };
            var evidence = new EmailCaseEvidence(
                [],
                [evidenceValue, bodyValue],
                [],
                [],
                [],
                [],
                [],
                []);

            var snapshot = await resolver.ResolveAsync(evidence, CancellationToken.None);

            Assert.Single(repository.ClaimValues);
            Assert.Equal("CLAIM-1", repository.ClaimValues[0]);
            Assert.Single(snapshot.ClaimResults);
        }

        [Fact]
        public void SnapshotClassifiesAgreementConflictAndPartialOverlapWithoutValues()
        {
            var agreement = Snapshot([1], [1], [1]).GetObserverClassifications();
            var conflict = Snapshot([1], [2], []).GetObserverClassifications();
            var partial = Snapshot([1, 2], [2, 3], []).GetObserverClassifications();

            Assert.Contains("PRIMARY_AGREEMENT", agreement);
            Assert.Contains("PRIMARY_CONFLICT", conflict);
            Assert.Contains("PARTIAL_OVERLAP", partial);
        }

        private static EmailCaseResolutionSnapshot Snapshot(
            int[] tik,
            int[] claim,
            int[] court)
            => new(
                tik.Length == 0 ? [] : [new(EmailEvidenceType.InternalTikNumber, "t", tik)],
                claim.Length == 0 ? [] : [new(EmailEvidenceType.ClaimNumber, "c", claim)],
                court.Length == 0 ? [] : [new(EmailEvidenceType.CourtCaseNumber, "o", court)]);

        private static void AssertResolution(
            EvidenceResolutionResult result,
            EmailEvidenceType type,
            string normalizedValue,
            params int[] counters)
        {
            Assert.Equal(type, result.EvidenceType);
            Assert.Equal(normalizedValue, result.NormalizedValue);
            Assert.Equal(counters, result.TikCounters);
            Assert.Equal(
                counters.Length switch
                {
                    0 => EvidenceResolutionStatus.NotFound,
                    1 => EvidenceResolutionStatus.Unique,
                    _ => EvidenceResolutionStatus.Ambiguous
                },
                result.Status);
        }

        private static OdcanitHozlapMainData Row(
            int tikCounter,
            string? visualId = null,
            string? additional = null,
            string? court = null,
            string? marker = null)
            => new()
            {
                TikCounter = tikCounter,
                VisualId = visualId,
                Additional = additional,
                clcCourtTikNum = court,
                CourtName = marker
            };

        private static OdcanitUserData UserData(
            int tikCounter,
            string fieldName,
            string? value = null,
            DateTime? date = null)
            => new()
            {
                TikCounter = tikCounter,
                PageName = "פרטי תיק נזיקין מליגל",
                FieldName = fieldName,
                strData = value,
                dateData = date
            };

        private static OdcanitSideDataLink Side(int tikCounter, int sideDataCounter)
            => new() { TikCounter = tikCounter, SideDataCounter = sideDataCounter };

        private static OdcanitClient Client(int sideCounter, string visualId)
            => new() { SideCounter = sideCounter, VisualID = visualId };

        private static RepositoryFixture CreateFixture(params OdcanitHozlapMainData[] rows)
        {
            var db = CreateTestDb();
            for (var index = 0; index < rows.Length; index++)
            {
                db.HozlapMainData.Add(rows[index]);
                db.Entry(rows[index]).Property<int>("SyntheticRowId").CurrentValue = index + 1;
            }
            db.SaveChanges();
            return new RepositoryFixture(db, new SqlEmailCaseResolutionRepository(db));
        }

        private static RepositoryFixture CreateSupportingFixture(
            IReadOnlyList<OdcanitUserData>? userData = null,
            IReadOnlyList<OdcanitSideDataLink>? sides = null,
            IReadOnlyList<OdcanitClient>? clients = null)
        {
            var db = CreateTestDb();
            for (var index = 0; index < (userData?.Count ?? 0); index++)
            {
                db.UserData.Add(userData![index]);
                db.Entry(userData[index]).Property<int>("SyntheticUserDataId").CurrentValue = index + 1;
            }
            for (var index = 0; index < (sides?.Count ?? 0); index++)
            {
                db.SideDataLinks.Add(sides![index]);
                db.Entry(sides[index]).Property<int>("SyntheticSideId").CurrentValue = index + 1;
            }
            for (var index = 0; index < (clients?.Count ?? 0); index++)
            {
                db.Clients.Add(clients![index]);
                db.Entry(clients[index]).Property<int>("SyntheticClientId").CurrentValue = index + 1;
            }
            db.SaveChanges();
            return new RepositoryFixture(db, new SqlEmailCaseResolutionRepository(db));
        }

        private static TestOdcanitDbContext CreateTestDb()
        {
            var options = new DbContextOptionsBuilder<OdcanitDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
                .Options;
            return new TestOdcanitDbContext(options);
        }

        private static OdcanitDbContext CreateModelDb()
        {
            var options = new DbContextOptionsBuilder<OdcanitDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
                .Options;
            return new OdcanitDbContext(options);
        }

        private sealed class TestOdcanitDbContext(DbContextOptions<OdcanitDbContext> options)
            : OdcanitDbContext(options)
        {
            protected override void OnModelCreating(ModelBuilder modelBuilder)
            {
                base.OnModelCreating(modelBuilder);
                modelBuilder.Entity<OdcanitHozlapMainData>()
                    .Property<int>("SyntheticRowId");
                modelBuilder.Entity<OdcanitHozlapMainData>()
                    .HasKey("SyntheticRowId");
                modelBuilder.Entity<OdcanitUserData>()
                    .Property<int>("SyntheticUserDataId");
                modelBuilder.Entity<OdcanitUserData>()
                    .HasKey("SyntheticUserDataId");
                modelBuilder.Entity<OdcanitSideDataLink>()
                    .Property<int>("SyntheticSideId");
                modelBuilder.Entity<OdcanitSideDataLink>()
                    .HasKey("SyntheticSideId");
                modelBuilder.Entity<OdcanitClient>()
                    .Property<int>("SyntheticClientId");
                modelBuilder.Entity<OdcanitClient>()
                    .HasKey("SyntheticClientId");
            }
        }

        private sealed class RepositoryFixture(
            TestOdcanitDbContext db,
            SqlEmailCaseResolutionRepository repository)
            : IAsyncDisposable
        {
            public SqlEmailCaseResolutionRepository Repository { get; } = repository;

            public ValueTask DisposeAsync() => db.DisposeAsync();
        }

        private sealed class CapturingRepository : IEmailCaseResolutionRepository
        {
            public IReadOnlyList<string> ClaimValues { get; private set; } = [];

            public Task<IReadOnlyList<EvidenceResolutionResult>> ResolveInternalTikNumbersAsync(
                IEnumerable<string> normalizedValues,
                CancellationToken cancellationToken)
                => Task.FromResult<IReadOnlyList<EvidenceResolutionResult>>([]);

            public Task<IReadOnlyList<EvidenceResolutionResult>> ResolveClaimNumbersAsync(
                IEnumerable<string> normalizedValues,
                CancellationToken cancellationToken)
            {
                ClaimValues = normalizedValues.ToArray();
                return Task.FromResult<IReadOnlyList<EvidenceResolutionResult>>(
                    [new(EmailEvidenceType.ClaimNumber, ClaimValues[0], [10])]);
            }

            public Task<IReadOnlyList<EvidenceResolutionResult>> ResolveCourtCaseNumbersAsync(
                IEnumerable<string> normalizedValues,
                CancellationToken cancellationToken)
                => Task.FromResult<IReadOnlyList<EvidenceResolutionResult>>([]);

            public Task<SupportingEvidenceFilterResult> FilterCandidatesByVehicleAsync(
                IEnumerable<int> candidateTikCounters,
                IEnumerable<string> normalizedValues,
                CancellationToken cancellationToken)
                => EmptySet();

            public Task<SupportingEvidenceFilterResult> FilterCandidatesByEventDateAsync(
                IEnumerable<int> candidateTikCounters,
                IEnumerable<string> normalizedValues,
                CancellationToken cancellationToken)
                => EmptySet();

            public Task<SupportingEvidenceFilterResult> FilterCandidatesByClientAsync(
                IEnumerable<int> candidateTikCounters,
                IEnumerable<string> normalizedValues,
                CancellationToken cancellationToken)
                => EmptySet();

            public Task<SupportingEvidenceFilterResult> FilterCandidatesByInsuredNameAsync(
                IEnumerable<int> candidateTikCounters,
                IEnumerable<string> normalizedValues,
                CancellationToken cancellationToken)
                => EmptySet();

            public Task<SupportingEvidenceFilterResult> FilterCandidatesByDriverPhoneAsync(
                IEnumerable<int> candidateTikCounters,
                IEnumerable<string> normalizedValues,
                CancellationToken cancellationToken)
                => EmptySet();

            private static Task<SupportingEvidenceFilterResult> EmptySet()
                => Task.FromResult(SupportingEvidenceFilterResult.Empty);
        }
    }
}
