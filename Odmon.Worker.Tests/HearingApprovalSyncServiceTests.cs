using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Odmon.Worker.Data;
using Odmon.Worker.Models;
using Odmon.Worker.Monday;
using Odmon.Worker.OdcanitAccess;
using Odmon.Worker.Services;
using Xunit;

namespace Odmon.Worker.Tests
{
    public class HearingApprovalSyncServiceTests
    {
        private const long BoardId = 5035534500;
        private const long ItemId = 2812699722;
        private const int TikCounter = 40514;
        private const string TikNumber = "9/1984";

        [Fact]
        public async Task MismatchedMappingTikNumber_SkipsWriteBackAndPersistsInvalidMappingFailure()
        {
            await using var db = CreateDb();
            const long boardId = 5035534500;
            db.MondayItemMappings.Add(new MondayItemMapping
            {
                TikCounter = 91003,
                TikNumber = "98/91003",
                BoardId = boardId,
                MondayItemId = 9000000004,
                CreatedAtUtc = DateTime.UtcNow
            });
            await db.SaveChangesAsync();

            var mondayClient = new FakeMondayClient();
            var odcanitWriter = new FakeOdcanitWriter();
            var mappingReader = new MondayMappingReadService(
                db,
                NullLogger<MondayMappingReadService>.Instance);
            var service = new HearingApprovalSyncService(
                db,
                mondayClient,
                odcanitWriter,
                new ConfigurationBuilder()
                    .AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["OdcanitWrites:Enable"] = "true",
                        ["OdcanitWrites:DryRun"] = "false"
                    })
                    .Build(),
                Options.Create(new MondaySettings { CasesBoardId = boardId }),
                NullLogger<HearingApprovalSyncService>.Instance,
                mappingReader);

            await service.SyncAsync(
                new[]
                {
                    new OdcanitCase
                    {
                        TikCounter = 91003,
                        TikNumber = "99/91003"
                    }
                },
                CancellationToken.None);

            Assert.Equal(0, odcanitWriter.AppendCount);
            Assert.Equal(0, mondayClient.GetHearingApprovalStatusCount);
            var failure = Assert.Single(db.SyncFailures);
            Assert.Equal("hearing_approval_skipped_invalid_mapping", failure.Operation);
            Assert.Equal("InvalidMapping", failure.ErrorType);
        }

        [Fact]
        public async Task ExistingLogicalWriteLogSkipsAnnexAndAdvancesKnownState()
        {
            await using var db = CreateDb();
            var existingCreatedAt = new DateTime(2026, 8, 30, 8, 0, 0, DateTimeKind.Utc);
            await SeedActionableTransitionAsync(db);
            db.NispahWriteLogs.Add(HearingApprovalSyncService.BuildWriteLog(
                TikCounter,
                TikNumber,
                ItemId,
                "אישר הגעה לדיון",
                existingCreatedAt,
                failed: false));
            await db.SaveChangesAsync();
            var writer = new FakeOdcanitWriter();
            var service = CreateService(db, writer);

            await service.SyncAsync([Case()], CancellationToken.None);

            Assert.Equal(0, writer.AppendCount);
            Assert.Single(db.NispahWriteLogs);
            var state = Assert.Single(db.MondayHearingApprovalStates);
            Assert.Equal("1", state.LastKnownStatus);
            Assert.Equal(existingCreatedAt, state.LastWriteAtUtc);
        }

        [Theory]
        [InlineData(2601)]
        [InlineData(2627)]
        public async Task ConcurrentLogicalWriteLogDuplicateIsIdempotentAndDetachesFailedEntity(
            int sqlErrorNumber)
        {
            await using var db = CreateThrowingDb();
            await SeedActionableTransitionAsync(db);
            var writer = new FakeOdcanitWriter();
            var service = CreateService(db, writer);
            db.NextNispahLogSaveException = DuplicateWriteLogException(sqlErrorNumber);

            await service.SyncAsync([Case()], CancellationToken.None);

            Assert.Equal(1, writer.AppendCount);
            Assert.Empty(db.NispahWriteLogs);
            Assert.DoesNotContain(
                db.ChangeTracker.Entries<NispahWriteLog>(),
                entry => entry.State == EntityState.Added);
            var state = Assert.Single(db.MondayHearingApprovalStates);
            Assert.Equal("1", state.LastKnownStatus);
            Assert.NotNull(state.LastWriteAtUtc);
        }

        [Fact]
        public async Task NonDuplicateWriteLogDbUpdateExceptionStillPropagates()
        {
            await using var db = CreateThrowingDb();
            await SeedActionableTransitionAsync(db);
            var writer = new FakeOdcanitWriter();
            var service = CreateService(db, writer);
            var sqlException = SqlExceptionTestHelper.Create(
                1205,
                "Synthetic deadlock while writing dbo.NispahWriteLogs.");
            db.NextNispahLogSaveException = new DbUpdateException(
                "Synthetic non-duplicate write-log failure.",
                sqlException);

            await Assert.ThrowsAsync<DbUpdateException>(() =>
                service.SyncAsync([Case()], CancellationToken.None));

            Assert.Equal(1, writer.AppendCount);
        }

        [Fact]
        public void UniqueViolationForAnotherTableIsNotAcceptedAsHearingApprovalDuplicate()
        {
            var sqlException = SqlExceptionTestHelper.Create(
                2601,
                "Cannot insert duplicate key row in object 'dbo.OtherTable'.");
            var exception = new DbUpdateException("Synthetic other-table duplicate.", sqlException);

            Assert.False(HearingApprovalSyncService.IsExpectedNispahWriteLogDuplicate(exception));
        }

        [Fact]
        public void DifferentUniqueIndexOnNispahWriteLogsIsNotAccepted()
        {
            var sqlException = SqlExceptionTestHelper.Create(
                2601,
                "Cannot insert duplicate key row in object 'dbo.NispahWriteLogs' with unique index 'IX_SyntheticOtherKey'.");
            var exception = new DbUpdateException("Synthetic other-index duplicate.", sqlException);

            Assert.False(HearingApprovalSyncService.IsExpectedNispahWriteLogDuplicate(exception));
        }

        private static async Task SeedActionableTransitionAsync(IntegrationDbContext db)
        {
            db.MondayItemMappings.Add(new MondayItemMapping
            {
                TikCounter = TikCounter,
                TikNumber = TikNumber,
                BoardId = BoardId,
                MondayItemId = ItemId,
                CreatedAtUtc = DateTime.UtcNow
            });
            db.MondayHearingApprovalStates.Add(new MondayHearingApprovalState
            {
                BoardId = BoardId,
                MondayItemId = ItemId,
                TikCounter = TikCounter,
                LastKnownStatus = "5",
                UpdatedAtUtc = DateTime.UtcNow
            });
            await db.SaveChangesAsync();
        }

        private static OdcanitCase Case()
            => new() { TikCounter = TikCounter, TikNumber = TikNumber };

        private static HearingApprovalSyncService CreateService(
            IntegrationDbContext db,
            FakeOdcanitWriter writer)
        {
            var mappingReader = new MondayMappingReadService(
                db,
                NullLogger<MondayMappingReadService>.Instance);
            return new HearingApprovalSyncService(
                db,
                new FakeMondayClient(),
                writer,
                new ConfigurationBuilder()
                    .AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["OdcanitWrites:Enable"] = "true",
                        ["OdcanitWrites:DryRun"] = "false"
                    })
                    .Build(),
                Options.Create(new MondaySettings { CasesBoardId = BoardId }),
                NullLogger<HearingApprovalSyncService>.Instance,
                mappingReader);
        }

        private static DbUpdateException DuplicateWriteLogException(int sqlErrorNumber)
        {
            var sqlException = SqlExceptionTestHelper.Create(
                sqlErrorNumber,
                "Cannot insert duplicate key row in object 'dbo.NispahWriteLogs' with unique index 'IX_NispahWriteLogs_TikCounter_NispahType_SourceItemId_InfoHash'.");
            return new DbUpdateException("Synthetic expected duplicate.", sqlException);
        }

        private static IntegrationDbContext CreateDb()
        {
            var options = new DbContextOptionsBuilder<IntegrationDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
                .Options;
            return new IntegrationDbContext(options);
        }

        private static ThrowingIntegrationDbContext CreateThrowingDb()
        {
            var options = new DbContextOptionsBuilder<IntegrationDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
                .Options;
            return new ThrowingIntegrationDbContext(options);
        }

        private sealed class ThrowingIntegrationDbContext(
            DbContextOptions<IntegrationDbContext> options)
            : IntegrationDbContext(options)
        {
            public DbUpdateException? NextNispahLogSaveException { get; set; }

            public override Task<int> SaveChangesAsync(
                CancellationToken cancellationToken = default)
            {
                if (NextNispahLogSaveException != null &&
                    ChangeTracker.Entries<NispahWriteLog>().Any(entry =>
                        entry.State == EntityState.Added))
                {
                    var exception = NextNispahLogSaveException;
                    NextNispahLogSaveException = null;
                    return Task.FromException<int>(exception);
                }

                return base.SaveChangesAsync(cancellationToken);
            }
        }

        private sealed class FakeOdcanitWriter : IOdcanitWriter
        {
            public int AppendCount { get; private set; }

            public Task AppendNispahAsync(OdcanitCase c, DateTime nowUtc, string nispahType, string info, CancellationToken ct)
            {
                AppendCount++;
                return Task.CompletedTask;
            }
        }

        private sealed class FakeMondayClient : IMondayClient
        {
            public int GetHearingApprovalStatusCount { get; private set; }

            public Task<IReadOnlyList<string>> GetBoardGroupIdsAsync(long boardId, CancellationToken ct)
                => Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());

            public Task<long> CreateItemAsync(long boardId, string groupId, string itemName, string columnValuesJson, CancellationToken ct)
                => throw new NotImplementedException();

            public Task<string?> GetItemStateAsync(long boardId, long itemId, CancellationToken ct)
                => Task.FromResult<string?>(null);

            public Task UpdateItemAsync(long boardId, long itemId, string columnValuesJson, CancellationToken ct)
                => Task.CompletedTask;

            public Task UpdateItemNameAsync(long boardId, long itemId, string name, CancellationToken ct)
                => Task.CompletedTask;

            public Task<long?> FindItemIdByColumnValueAsync(long boardId, string columnId, string columnValue, CancellationToken ct)
                => Task.FromResult<long?>(null);

            public Task<string?> GetHearingApprovalStatusAsync(long itemId, CancellationToken ct)
            {
                GetHearingApprovalStatusCount++;
                return Task.FromResult<string?>("1");
            }

            public Task UpdateHearingDetailsAsync(long boardId, long itemId, string judgeName, string city, string judgeColumnId, string cityColumnId, CancellationToken ct)
                => Task.CompletedTask;

            public Task UpdateHearingDateAsync(long boardId, long itemId, DateTime startDate, string dateColumnId, string hourColumnId, CancellationToken ct)
                => Task.CompletedTask;

            public Task UpdateHearingStatusAsync(long boardId, long itemId, string label, string statusColumnId, CancellationToken ct)
                => Task.CompletedTask;
        }
    }
}
