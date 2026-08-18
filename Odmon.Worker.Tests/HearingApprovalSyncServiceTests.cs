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

        private static IntegrationDbContext CreateDb()
        {
            var options = new DbContextOptionsBuilder<IntegrationDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
                .Options;
            return new IntegrationDbContext(options);
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
