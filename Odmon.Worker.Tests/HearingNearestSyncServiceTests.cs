using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Odmon.Worker.Data;
using Odmon.Worker.Monday;
using Odmon.Worker.Models;
using Odmon.Worker.OdcanitAccess;
using Odmon.Worker.Services;
using Xunit;

namespace Odmon.Worker.Tests
{
    public sealed class HearingNearestSyncServiceTests
    {
        private const long BoardId = 700001;
        private const long MondayItemId = 800001;
        private const int TikCounter = 1001;
        private const string TikNumber = "SYN-1001";
        private const string CancelledLabel = "מבוטל";
        private const string TransferredLabel = "הועבר";

        [Fact]
        public async Task ExistingMapping_NotReady_NoSnapshot_FutureCancelled_UpdatesStatusThenSavesSnapshot()
        {
            await using var scenario = CreateScenario(meetStatus: 1);

            await scenario.Service.SyncNearestHearingsAsync(BoardId, CancellationToken.None);

            Assert.Equal(new[] { CancelledLabel }, scenario.Monday.StatusLabels);
            Assert.Equal(0, scenario.Reader.GetCasesCallCount);
            var snapshot = Assert.Single(await scenario.Db.HearingNearestSnapshots.ToListAsync());
            Assert.Equal(1, snapshot.NearestMeetStatus);
        }

        [Fact]
        public async Task ExistingMapping_CreatedBeforeListenerT0_IsStillReconciled()
        {
            var listenerStart = DateTime.UtcNow.AddDays(-10);
            await using var scenario = CreateScenario(
                meetStatus: 1,
                mappingCreatedAtUtc: listenerStart.AddDays(-20),
                listenerStartedAtUtc: listenerStart);

            await scenario.Service.SyncNearestHearingsAsync(BoardId, CancellationToken.None);

            Assert.Equal(new[] { CancelledLabel }, scenario.Monday.StatusLabels);
            Assert.Single(await scenario.Db.HearingNearestSnapshots.ToListAsync());
        }

        [Fact]
        public async Task FutureTransferredOnly_UpdatesTransferredStatus()
        {
            await using var scenario = CreateScenario(meetStatus: 2);

            await scenario.Service.SyncNearestHearingsAsync(BoardId, CancellationToken.None);

            Assert.Equal(new[] { TransferredLabel }, scenario.Monday.StatusLabels);
            Assert.Equal(2, Assert.Single(await scenario.Db.HearingNearestSnapshots.ToListAsync()).NearestMeetStatus);
        }

        [Fact]
        public async Task ActiveHearing_NeverWritesActiveStatus()
        {
            await using var scenario = CreateScenario(meetStatus: 0);

            await scenario.Service.SyncNearestHearingsAsync(BoardId, CancellationToken.None);

            Assert.Empty(scenario.Monday.StatusLabels);
            Assert.Equal(0, Assert.Single(await scenario.Db.HearingNearestSnapshots.ToListAsync()).NearestMeetStatus);
        }

        [Fact]
        public async Task MissingCancellationLabel_DoesNotSaveSnapshot()
        {
            await using var scenario = CreateScenario(
                meetStatus: 1,
                allowedLabels: new[] { TransferredLabel });

            await scenario.Service.SyncNearestHearingsAsync(BoardId, CancellationToken.None);

            Assert.Empty(scenario.Monday.StatusLabels);
            Assert.Empty(await scenario.Db.HearingNearestSnapshots.ToListAsync());
            Assert.Equal(1, scenario.SkipLogger.CallCount);
        }

        [Fact]
        public async Task StatusMutationFailure_DoesNotSaveSnapshot()
        {
            await using var scenario = CreateScenario(meetStatus: 1, failStatusMutation: true);

            await scenario.Service.SyncNearestHearingsAsync(BoardId, CancellationToken.None);

            Assert.Empty(await scenario.Db.HearingNearestSnapshots.ToListAsync());
        }

        [Fact]
        public async Task LaterRequiredMutationFailure_DoesNotAdvanceSnapshot()
        {
            await using var scenario = CreateScenario(
                meetStatus: 1,
                includeHearingDetails: true,
                failDateMutation: true);

            await scenario.Service.SyncNearestHearingsAsync(BoardId, CancellationToken.None);

            Assert.Equal(new[] { CancelledLabel }, scenario.Monday.StatusLabels);
            Assert.Empty(await scenario.Db.HearingNearestSnapshots.ToListAsync());
        }

        [Fact]
        public async Task DryRun_DoesNotMutateMondayOrSaveSnapshot()
        {
            await using var scenario = CreateScenario(meetStatus: 1, dryRun: true);

            await scenario.Service.SyncNearestHearingsAsync(BoardId, CancellationToken.None);

            Assert.Empty(scenario.Monday.StatusLabels);
            Assert.Empty(await scenario.Db.HearingNearestSnapshots.ToListAsync());
        }

        [Fact]
        public async Task RecentlyElapsedCancelled_InsideRecoveryWindow_IsReconciled()
        {
            await using var scenario = CreateScenario(meetStatus: 1, hearingStartOffset: TimeSpan.FromDays(-2));

            await scenario.Service.SyncNearestHearingsAsync(BoardId, CancellationToken.None);

            Assert.Equal(new[] { CancelledLabel }, scenario.Monday.StatusLabels);
            Assert.Single(await scenario.Db.HearingNearestSnapshots.ToListAsync());
        }

        [Fact]
        public async Task RecentlyElapsedTransferred_InsideRecoveryWindow_IsReconciled()
        {
            await using var scenario = CreateScenario(meetStatus: 2, hearingStartOffset: TimeSpan.FromHours(-4));

            await scenario.Service.SyncNearestHearingsAsync(BoardId, CancellationToken.None);

            Assert.Equal(new[] { TransferredLabel }, scenario.Monday.StatusLabels);
            Assert.Single(await scenario.Db.HearingNearestSnapshots.ToListAsync());
        }

        [Fact]
        public async Task PastActive_IsNotReconciled()
        {
            await using var scenario = CreateScenario(meetStatus: 0, hearingStartOffset: TimeSpan.FromHours(-1));

            await scenario.Service.SyncNearestHearingsAsync(BoardId, CancellationToken.None);

            Assert.Empty(scenario.Monday.StatusLabels);
            Assert.Empty(await scenario.Db.HearingNearestSnapshots.ToListAsync());
        }

        [Fact]
        public async Task ElapsedCancelled_OutsideRecoveryWindow_IsNotReconciled()
        {
            await using var scenario = CreateScenario(meetStatus: 1, hearingStartOffset: TimeSpan.FromDays(-31));

            await scenario.Service.SyncNearestHearingsAsync(BoardId, CancellationToken.None);

            Assert.Empty(scenario.Monday.StatusLabels);
            Assert.Empty(await scenario.Db.HearingNearestSnapshots.ToListAsync());
        }

        private static Scenario CreateScenario(
            int meetStatus,
            DateTime? mappingCreatedAtUtc = null,
            DateTime? listenerStartedAtUtc = null,
            IEnumerable<string>? allowedLabels = null,
            bool failStatusMutation = false,
            TimeSpan? hearingStartOffset = null,
            bool includeHearingDetails = false,
            bool failDateMutation = false,
            bool dryRun = false)
        {
            var options = new DbContextOptionsBuilder<IntegrationDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
                .Options;
            var db = new IntegrationDbContext(options);
            db.MondayItemMappings.Add(new MondayItemMapping
            {
                TikCounter = TikCounter,
                TikNumber = TikNumber,
                MondayItemId = MondayItemId,
                BoardId = BoardId,
                CreatedAtUtc = mappingCreatedAtUtc ?? DateTime.UtcNow.AddDays(-1)
            });
            if (listenerStartedAtUtc.HasValue)
            {
                db.ListenerStates.Add(new ListenerState
                {
                    Id = 1,
                    StartedAtUtc = listenerStartedAtUtc.Value,
                    UpdatedAtUtc = listenerStartedAtUtc.Value
                });
            }
            db.SaveChanges();

            var israelTime = TimeZoneInfo.FindSystemTimeZoneById("Israel Standard Time");
            var hearingStartLocal = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, israelTime)
                .Add(hearingStartOffset ?? TimeSpan.FromDays(2));
            var reader = new FakeOdcanitReader(new OdcanitDiaryEvent
            {
                TikCounter = TikCounter,
                StartDate = hearingStartLocal,
                MeetStatus = meetStatus,
                JudgeName = includeHearingDetails ? "Synthetic Judge" : null,
                City = includeHearingDetails ? "Synthetic City" : null
            });
            var monday = new FakeMondayClient
            {
                FailStatusMutation = failStatusMutation,
                FailDateMutation = failDateMutation
            };
            var metadata = new FakeMetadataProvider(allowedLabels ?? new[] { CancelledLabel, TransferredLabel });
            var skipLogger = new FakeSkipLogger();
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Testing:Enable"] = "false",
                    ["OdcanitWrites:Enable"] = "true",
                    ["OdcanitWrites:DryRun"] = dryRun.ToString(),
                    ["HearingNearest:RecoveryLookbackDays"] = "30"
                })
                .Build();
            var mondaySettings = Options.Create(new MondaySettings
            {
                HearingStatusColumnId = "synthetic_status",
                JudgeNameColumnId = "synthetic_judge",
                HearingDateColumnId = "synthetic_date",
                HearingHourColumnId = "synthetic_hour"
            });
            var mappingReader = new MondayMappingReadService(
                db,
                NullLogger<MondayMappingReadService>.Instance);
            var service = new HearingNearestSyncService(
                reader,
                db,
                monday,
                metadata,
                configuration,
                mondaySettings,
                NullLogger<HearingNearestSyncService>.Instance,
                skipLogger,
                mappingReader);

            return new Scenario(db, service, reader, monday, skipLogger);
        }

        private sealed class Scenario(
            IntegrationDbContext db,
            HearingNearestSyncService service,
            FakeOdcanitReader reader,
            FakeMondayClient monday,
            FakeSkipLogger skipLogger) : IAsyncDisposable
        {
            public IntegrationDbContext Db { get; } = db;
            public HearingNearestSyncService Service { get; } = service;
            public FakeOdcanitReader Reader { get; } = reader;
            public FakeMondayClient Monday { get; } = monday;
            public FakeSkipLogger SkipLogger { get; } = skipLogger;

            public ValueTask DisposeAsync() => Db.DisposeAsync();
        }

        private sealed class FakeOdcanitReader(OdcanitDiaryEvent hearing) : IOdcanitReader
        {
            public int GetCasesCallCount { get; private set; }

            public Task<List<OdcanitCase>> GetCasesCreatedOnDateAsync(DateTime date, CancellationToken ct)
                => Task.FromResult(new List<OdcanitCase>());

            public Task<List<OdcanitCase>> GetCasesByTikCountersAsync(IEnumerable<int> tikCounters, CancellationToken ct)
            {
                GetCasesCallCount++;
                return Task.FromResult(new List<OdcanitCase>
                {
                    new() { TikCounter = TikCounter, TikNumber = TikNumber, IsReadyForMonday = false }
                });
            }

            public Task<List<OdcanitDiaryEvent>> GetDiaryEventsByTikCountersAsync(
                IEnumerable<int> tikCounters,
                CancellationToken ct)
                => Task.FromResult(new List<OdcanitDiaryEvent> { hearing });

            public Task<Dictionary<string, int>> ResolveTikNumbersToCountersAsync(
                IEnumerable<string> tikNumbers,
                CancellationToken ct)
                => Task.FromResult(new Dictionary<string, int>(StringComparer.Ordinal)
                {
                    [TikNumber] = TikCounter
                });

            public Task<List<int>> GetTikCountersSinceCutoffAsync(DateTime cutoffDate, CancellationToken ct)
                => Task.FromResult(new List<int>());
        }

        private sealed class FakeMondayClient : IMondayClient
        {
            public List<string> StatusLabels { get; } = new();
            public bool FailStatusMutation { get; init; }
            public bool FailDateMutation { get; init; }

            public Task UpdateHearingStatusAsync(long boardId, long itemId, string label, string statusColumnId, CancellationToken ct)
            {
                if (FailStatusMutation)
                {
                    throw new InvalidOperationException("Synthetic Monday failure");
                }
                StatusLabels.Add(label);
                return Task.CompletedTask;
            }

            public Task UpdateHearingDetailsAsync(long boardId, long itemId, string judgeName, string city, string judgeColumnId, string cityColumnId, CancellationToken ct)
                => Task.CompletedTask;
            public Task UpdateHearingDateAsync(long boardId, long itemId, DateTime startDate, string dateColumnId, string hourColumnId, CancellationToken ct)
            {
                if (FailDateMutation)
                {
                    throw new InvalidOperationException("Synthetic Monday date failure");
                }
                return Task.CompletedTask;
            }
            public Task<IReadOnlyList<string>> GetBoardGroupIdsAsync(long boardId, CancellationToken ct)
                => Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());
            public Task<long> CreateItemAsync(long boardId, string groupId, string itemName, string columnValuesJson, CancellationToken ct)
                => Task.FromResult(0L);
            public Task<string?> GetItemStateAsync(long boardId, long itemId, CancellationToken ct)
                => Task.FromResult<string?>("active");
            public Task<MondayItemStatusValue?> GetItemStatusValueAsync(long boardId, long itemId, string statusColumnId, CancellationToken ct)
                => Task.FromResult<MondayItemStatusValue?>(new(boardId, itemId, "active", null));
            public Task UpdateItemAsync(long boardId, long itemId, string columnValuesJson, CancellationToken ct)
                => Task.CompletedTask;
            public Task UpdateItemNameAsync(long boardId, long itemId, string name, CancellationToken ct)
                => Task.CompletedTask;
            public Task<long?> FindItemIdByColumnValueAsync(long boardId, string columnId, string columnValue, CancellationToken ct)
                => Task.FromResult<long?>(null);
            public Task<string?> GetHearingApprovalStatusAsync(long itemId, CancellationToken ct)
                => Task.FromResult<string?>(null);
        }

        private sealed class FakeMetadataProvider(IEnumerable<string> allowedLabels) : IMondayMetadataProvider
        {
            private readonly HashSet<string> _allowedLabels = new(allowedLabels, StringComparer.Ordinal);

            public Task<HashSet<string>> GetAllowedStatusLabelsAsync(long boardId, string columnId, CancellationToken ct = default)
                => Task.FromResult(new HashSet<string>(_allowedLabels, StringComparer.Ordinal));
            public Task<HashSet<string>> GetAllowedDropdownLabelsAsync(long boardId, string columnId, CancellationToken ct = default)
                => Task.FromResult(new HashSet<string>(StringComparer.Ordinal));
            public Task<string> GetColumnIdByTitleAsync(long boardId, string title, CancellationToken ct = default)
                => Task.FromResult(string.Empty);
            public Task<string?> GetColumnTypeAsync(long boardId, string columnId, CancellationToken ct = default)
                => Task.FromResult<string?>(null);
            public Task<Dictionary<string, BoardColumnMetadata>> GetBoardColumnsMetadataAsync(long boardId, CancellationToken ct = default)
                => Task.FromResult(new Dictionary<string, BoardColumnMetadata>(StringComparer.Ordinal));
        }

        private sealed class FakeSkipLogger : ISkipLogger
        {
            public int CallCount { get; private set; }

            public Task LogSkipAsync(
                int tikCounter,
                string? tikNumber,
                string operation,
                string reasonCode,
                string? entityId,
                string? rawValue,
                object? details,
                CancellationToken ct)
            {
                CallCount++;
                return Task.CompletedTask;
            }
        }
    }
}
