using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Odmon.Worker.Data;
using Odmon.Worker.Models;
using Odmon.Worker.OdcanitAccess;
using Odmon.Worker.Services;
using Xunit;

namespace Odmon.Worker.Tests
{
    public class MondayItemMappingIntegrityTests
    {
        [Fact]
        public void BackfillMappingFactory_RejectsNegativeTikCounter()
        {
            var ex = Assert.Throws<MondayItemMappingIntegrityException>(() =>
                HearingBackfillService.CreateValidatedMapping(
                    boardId: 5035534500,
                    mondayItemId: 123456,
                    tikNumber: "99999-01-99",
                    tikCounter: -1,
                    createdAtUtc: DateTime.UtcNow));

            Assert.Contains("real positive Odcanit counter", ex.Message);
        }

        [Fact]
        public void BackfillMappingFactory_CreatesMappingOnlyWithRealPositiveCounter()
        {
            var mapping = HearingBackfillService.CreateValidatedMapping(
                boardId: 5035534500,
                mondayItemId: 123456,
                tikNumber: "99999-01-99",
                tikCounter: 91001,
                createdAtUtc: DateTime.UtcNow);

            Assert.Equal(91001, mapping.TikCounter);
            Assert.Equal("99999-01-99", mapping.TikNumber);
        }

        [Fact]
        public async Task IntegrityScan_FlagsRegression_WhenMappingHasFakeCounterButOdcanitHasRealCounter()
        {
            await using var db = CreateDb();
            db.MondayItemMappings.Add(new MondayItemMapping
            {
                Id = 1,
                BoardId = 5035534500,
                MondayItemId = 555,
                TikNumber = "99999-01-99",
                TikCounter = -7,
                CreatedAtUtc = DateTime.UtcNow
            });
            await db.SaveChangesAsync();

            var reader = new FakeOdcanitReader
            {
                ResolvedTikNumbers = { ["99999-01-99"] = 91001 }
            };
            var service = new MondayItemMappingIntegrityService(
                db,
                reader,
                NullLogger<MondayItemMappingIntegrityService>.Instance);

            var issues = await service.FindIntegrityIssuesAsync(CancellationToken.None);

            Assert.Contains(issues, i => i.Reason.Contains("real positive Odcanit counter", StringComparison.Ordinal));
            Assert.Contains(issues, i => i.Reason.Contains("Expected real TikCounter=91001", StringComparison.Ordinal));
        }

        [Fact]
        public async Task IntegrityScan_FlagsMappingWhereTikNumberResolvesToDifferentRealCounter()
        {
            await using var db = CreateDb();
            db.MondayItemMappings.Add(new MondayItemMapping
            {
                Id = 2,
                BoardId = 5035534500,
                MondayItemId = 777,
                TikNumber = "99999-01-99",
                TikCounter = 11111,
                CreatedAtUtc = DateTime.UtcNow
            });
            await db.SaveChangesAsync();

            var reader = new FakeOdcanitReader
            {
                ResolvedTikNumbers = { ["99999-01-99"] = 91001 }
            };
            var service = new MondayItemMappingIntegrityService(
                db,
                reader,
                NullLogger<MondayItemMappingIntegrityService>.Instance);

            var issues = await service.FindIntegrityIssuesAsync(CancellationToken.None);

            var issue = Assert.Single(issues);
            Assert.Contains("Expected real TikCounter=91001", issue.Reason);
        }

        [Fact]
        public async Task IntegrityScan_DoesNotFailPositiveMapping_WhenTikNumberIsUnresolved()
        {
            await using var db = CreateDb();
            db.MondayItemMappings.Add(new MondayItemMapping
            {
                Id = 3,
                BoardId = 5035534500,
                MondayItemId = 888,
                TikNumber = "101/15",
                TikCounter = 91001,
                CreatedAtUtc = DateTime.UtcNow
            });
            await db.SaveChangesAsync();

            var service = new MondayItemMappingIntegrityService(
                db,
                new FakeOdcanitReader(),
                NullLogger<MondayItemMappingIntegrityService>.Instance);

            var issues = await service.FindIntegrityIssuesAsync(CancellationToken.None);

            Assert.Empty(issues);
        }

        [Fact]
        public void MappingMatchedToCase_RejectsVisualIdMismatch()
        {
            var mapping = new MondayItemMapping
            {
                BoardId = 5035534500,
                MondayItemId = 777,
                TikNumber = "5/100",
                TikCounter = 91001,
                CreatedAtUtc = DateTime.UtcNow
            };
            var odcanitCase = new OdcanitCase
            {
                TikCounter = 91001,
                TikNumber = "5/200"
            };

            var ex = Assert.Throws<MondayItemMappingIntegrityException>(() =>
                MondayItemMappingIntegrityService.ValidateMappingMatchesCase(
                    mapping,
                    odcanitCase,
                    expectedBoardId: 5035534500,
                    source: "test"));

            Assert.Contains("TikNumber mismatch", ex.Message);
        }

        [Fact]
        public void ValidMappedCancelledHearing_PlansStatusUpdate()
        {
            var mapping = HearingBackfillService.CreateValidatedMapping(
                boardId: 5035534500,
                mondayItemId: 123456,
                tikNumber: "5/100",
                tikCounter: 91001,
                createdAtUtc: DateTime.UtcNow);
            var odcanitCase = new OdcanitCase
            {
                TikCounter = 91001,
                TikNumber = "5/100"
            };
            MondayItemMappingIntegrityService.ValidateMappingMatchesCase(
                mapping,
                odcanitCase,
                expectedBoardId: 5035534500,
                source: "test");

            var start = DateTime.UtcNow.AddDays(1);
            var snapshot = new HearingNearestSnapshot
            {
                NearestStartDateUtc = start,
                NearestMeetStatus = 0,
                JudgeName = "Judge",
                City = "City"
            };
            var hearing = new OdcanitDiaryEvent
            {
                TikCounter = 91001,
                StartDate = start,
                JudgeName = "Judge",
                City = "City",
                MeetStatus = 1
            };

            var (planned, _, _, _) = HearingNearestSyncServiceHelper.ComputePlannedSteps(hearing, snapshot);

            Assert.Single(planned);
            Assert.StartsWith("SetStatus_", planned[0], StringComparison.Ordinal);
            Assert.DoesNotContain("UpdateHearingDate", planned);
        }

        [Fact]
        public void ValidMappedTransferredHearing_PlansStatusUpdateBeforeDetails()
        {
            var start = DateTime.UtcNow.AddDays(1);
            var snapshot = new HearingNearestSnapshot
            {
                NearestStartDateUtc = start,
                NearestMeetStatus = 0,
                JudgeName = "OldJudge",
                City = "OldCity"
            };
            var hearing = new OdcanitDiaryEvent
            {
                TikCounter = 91001,
                StartDate = start.AddDays(1),
                JudgeName = "NewJudge",
                City = "NewCity",
                MeetStatus = 2
            };

            var (planned, _, _, _) = HearingNearestSyncServiceHelper.ComputePlannedSteps(hearing, snapshot);

            Assert.StartsWith("SetStatus_", planned[0], StringComparison.Ordinal);
            Assert.Contains("UpdateHearingDate", planned);
        }

        private static IntegrationDbContext CreateDb()
        {
            var options = new DbContextOptionsBuilder<IntegrationDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
                .Options;
            return new IntegrationDbContext(options);
        }

        private sealed class FakeOdcanitReader : IOdcanitReader
        {
            public Dictionary<string, int> ResolvedTikNumbers { get; } = new(StringComparer.Ordinal);

            public Task<List<OdcanitCase>> GetCasesCreatedOnDateAsync(DateTime date, CancellationToken ct)
                => Task.FromResult(new List<OdcanitCase>());

            public Task<List<OdcanitCase>> GetCasesByTikCountersAsync(IEnumerable<int> tikCounters, CancellationToken ct)
                => Task.FromResult(new List<OdcanitCase>());

            public Task<List<OdcanitDiaryEvent>> GetDiaryEventsByTikCountersAsync(IEnumerable<int> tikCounters, CancellationToken ct)
                => Task.FromResult(new List<OdcanitDiaryEvent>());

            public Task<Dictionary<string, int>> ResolveTikNumbersToCountersAsync(IEnumerable<string> tikNumbers, CancellationToken ct)
            {
                var result = tikNumbers
                    .Where(t => ResolvedTikNumbers.ContainsKey(t))
                    .ToDictionary(t => t, t => ResolvedTikNumbers[t], StringComparer.Ordinal);

                return Task.FromResult(result);
            }

            public Task<List<int>> GetTikCountersSinceCutoffAsync(DateTime cutoffDate, CancellationToken ct)
                => Task.FromResult(new List<int>());
        }
    }
}
