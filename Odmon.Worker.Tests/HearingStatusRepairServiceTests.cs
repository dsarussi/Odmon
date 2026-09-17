using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Odmon.Worker.Data;
using Odmon.Worker.Monday;
using Odmon.Worker.Models;
using Odmon.Worker.OdcanitAccess;
using Odmon.Worker.Services;
using Xunit;

namespace Odmon.Worker.Tests;

public sealed class HearingStatusRepairServiceTests
{
    private static readonly DateTimeOffset NowUtc =
        new(2026, 9, 17, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void AllowList_HasExactRequiredShape()
    {
        var targets = HearingStatusRepairService.GetTargets();

        HearingStatusRepairService.ValidateAllowList(targets);

        Assert.Equal(49, targets.Count);
        Assert.Equal(49, targets.Select(target => target.MondayItemId).Distinct().Count());
        Assert.Equal(44, targets.Count(target => target.ExpectedMeetStatus == 1));
        Assert.Equal(5, targets.Count(target => target.ExpectedMeetStatus == 2));
    }

    [Fact]
    public void AllowList_InvalidShape_FailsClosed()
    {
        var invalid = HearingStatusRepairService.GetTargets().Skip(1).ToArray();

        Assert.Throws<InvalidOperationException>(() =>
            HearingStatusRepairService.ValidateAllowList(invalid));
    }

    [Fact]
    public void Cli_RequiresExplicitUnambiguousModeAndLiveConfirmation()
    {
        Assert.Throws<ArgumentException>(() =>
            HearingStatusRepairCli.TryParse([HearingStatusRepairCli.CommandOption], out _));
        Assert.Throws<ArgumentException>(() => HearingStatusRepairCli.TryParse(
            [HearingStatusRepairCli.CommandOption, "--mode", "unknown"], out _));
        Assert.Throws<ArgumentException>(() => HearingStatusRepairCli.TryParse(
            [HearingStatusRepairCli.CommandOption, "--mode", "Live"], out _));
        Assert.Throws<ArgumentException>(() => HearingStatusRepairCli.TryParse(
            [HearingStatusRepairCli.CommandOption, "--mode", "DryRun", "--confirm-live", HearingStatusRepairCli.LiveConfirmation], out _));

        Assert.True(HearingStatusRepairCli.TryParse(
            [HearingStatusRepairCli.CommandOption, "--mode", "DryRun"], out var dryRun));
        Assert.Equal(HearingStatusRepairMode.DryRun, dryRun.Mode);
        Assert.True(HearingStatusRepairCli.TryParse(
            [HearingStatusRepairCli.CommandOption, "--mode", "Live", "--confirm-live", HearingStatusRepairCli.LiveConfirmation], out var live));
        Assert.Equal(HearingStatusRepairMode.Live, live.Mode);
    }

    [Fact]
    public void IsolatedHost_RegistersNoHostedWorkerOrOrdinaryReconciliation()
    {
        using var host = HearingStatusRepairCli.BuildIsolatedHost(
        [
            HearingStatusRepairCli.CommandOption,
            "--mode", "DryRun",
            "--ConnectionStrings:IntegrationDb", "Server=localhost;Database=Integration;Integrated Security=true",
            "--ConnectionStrings:OdcanitDb", "Server=localhost;Database=Odcanit;Integrated Security=true"
        ]);

        Assert.Empty(host.Services.GetServices<IHostedService>());
        Assert.Null(host.Services.GetService<SyncService>());
        Assert.Null(host.Services.GetService<HearingNearestSyncService>());
        Assert.Null(host.Services.GetService<HearingBackfillService>());
        using var scope = host.Services.CreateScope();
        Assert.NotNull(scope.ServiceProvider.GetService<HearingStatusRepairService>());
    }

    [Fact]
    public async Task DryRun_ValidatesAllItems_PlansOnlyAndLeavesSnapshotsUnchanged()
    {
        await using var scenario = await CreateScenarioAsync();

        var summary = await scenario.Service.RunAsync(
            HearingStatusRepairMode.DryRun,
            CancellationToken.None);

        Assert.Equal(49, summary.Allowlisted);
        Assert.Equal(49, summary.SourceValidated);
        Assert.Equal(49, summary.Planned);
        Assert.Equal(0, summary.AlreadyCorrect);
        Assert.Equal(0, summary.Updated);
        Assert.Equal(0, summary.ValidationFailed);
        Assert.Equal(0, summary.MondayFailed);
        Assert.Equal(0, summary.VerificationFailed);
        Assert.Empty(scenario.Monday.StatusMutations);
        Assert.Equal(0, summary.SnapshotStatusRecorded);
        Assert.Equal(49, summary.SnapshotUnchanged);
        Assert.Empty(await scenario.Db.HearingNearestSnapshots.ToListAsync());
    }

    [Fact]
    public async Task Live_UsesOnlyAllowlistedStatusMutationPath_AndSkipsAlreadyCorrect()
    {
        var alreadyCorrect = HearingStatusRepairService.GetTargets()[0];
        await using var scenario = await CreateScenarioAsync(
            currentLabels: new Dictionary<long, string?>
            {
                [alreadyCorrect.MondayItemId] = alreadyCorrect.ExpectedLabel
            });

        var summary = await scenario.Service.RunAsync(
            HearingStatusRepairMode.Live,
            CancellationToken.None);

        Assert.Equal(48, summary.Planned);
        Assert.Equal(1, summary.AlreadyCorrect);
        Assert.Equal(48, summary.Updated);
        Assert.Equal(0, summary.VerificationFailed);
        Assert.Equal(48, scenario.Monday.StatusMutations.Count);
        Assert.All(scenario.Monday.StatusMutations, mutation =>
        {
            Assert.Equal(HearingStatusRepairService.TargetBoardId, mutation.BoardId);
            Assert.Contains(mutation.ItemId, HearingStatusRepairService.GetTargets().Select(target => target.MondayItemId));
            Assert.Equal(HearingStatusRepairService.StatusColumnId, mutation.ColumnId);
            Assert.Contains(mutation.Label, new[]
            {
                HearingStatusRepairService.CancelledLabel,
                HearingStatusRepairService.TransferredLabel
            });
        });
        Assert.Equal(0, scenario.Monday.OtherMutationCount);
        Assert.Empty(await scenario.Db.HearingNearestSnapshots.ToListAsync());
    }

    [Fact]
    public async Task RuntimeValidation_SkipsChangedStatusAndInactiveItem()
    {
        var targets = HearingStatusRepairService.GetTargets();
        var changedSource = targets[0];
        var inactive = targets[1];
        await using var scenario = await CreateScenarioAsync(
            statusOverrides: new Dictionary<long, int>
            {
                [changedSource.MondayItemId] = 1
            },
            inactiveItemIds: new HashSet<long> { inactive.MondayItemId });

        var summary = await scenario.Service.RunAsync(
            HearingStatusRepairMode.Live,
            CancellationToken.None);

        Assert.Equal(2, summary.ValidationFailed);
        Assert.DoesNotContain(scenario.Monday.StatusMutations, mutation =>
            mutation.ItemId == changedSource.MondayItemId || mutation.ItemId == inactive.MondayItemId);
        Assert.Equal(47, summary.Updated);
    }

    [Fact]
    public async Task MissingRequiredLabel_AbortsBeforeAnyMutation()
    {
        await using var scenario = await CreateScenarioAsync(
            allowedLabels: new[] { HearingStatusRepairService.CancelledLabel });

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            scenario.Service.RunAsync(HearingStatusRepairMode.Live, CancellationToken.None));

        Assert.Empty(scenario.Monday.StatusMutations);
    }

    [Fact]
    public async Task MondayFailure_IsIndependentAndNeverAdvancesSnapshot()
    {
        var failedItem = HearingStatusRepairService.GetTargets()[0].MondayItemId;
        await using var scenario = await CreateScenarioAsync(failedMutationItemId: failedItem);

        var summary = await scenario.Service.RunAsync(
            HearingStatusRepairMode.Live,
            CancellationToken.None);

        Assert.Equal(48, summary.Updated);
        Assert.Equal(1, summary.MondayFailed);
        Assert.Equal(failedItem, Assert.Single(summary.MondayFailures).MondayItemId);
        Assert.Empty(await scenario.Db.HearingNearestSnapshots.ToListAsync());
    }

    [Fact]
    public async Task RetryableThrottle_RetriesThenVerifiesSuccess()
    {
        var itemId = HearingStatusRepairService.GetTargets()[0].MondayItemId;
        var failures = new Dictionary<long, Queue<Exception>>
        {
            [itemId] = new Queue<Exception>(
            [
                new MondayApiException(
                    "Synthetic throttle.",
                    errorCode: "COMPLEXITY_BUDGET_EXHAUSTED",
                    httpStatusCode: 429,
                    retryAfter: TimeSpan.FromSeconds(4))
            ])
        };
        await using var scenario = await CreateScenarioAsync(mutationFailures: failures);

        var summary = await scenario.Service.RunAsync(HearingStatusRepairMode.Live, CancellationToken.None);

        Assert.Equal(49, summary.Updated);
        Assert.Equal(0, summary.MondayFailed);
        Assert.Equal(2, scenario.Monday.StatusMutations.Count(mutation => mutation.ItemId == itemId));
        Assert.Contains(TimeSpan.FromSeconds(4), scenario.Delay.Delays);
    }

    [Fact]
    public async Task RetryableThrottle_ExhaustionIsMondayFailure()
    {
        var itemId = HearingStatusRepairService.GetTargets()[0].MondayItemId;
        var failures = new Dictionary<long, Queue<Exception>>
        {
            [itemId] = new Queue<Exception>(Enumerable.Range(0, 3).Select(_ =>
                new MondayApiException(
                    "Synthetic throttle.",
                    errorCode: "IP_RATE_LIMIT_EXCEEDED",
                    httpStatusCode: 429,
                    retryAfter: TimeSpan.FromSeconds(1))))
        };
        await using var scenario = await CreateScenarioAsync(mutationFailures: failures);

        var summary = await scenario.Service.RunAsync(HearingStatusRepairMode.Live, CancellationToken.None);

        Assert.Equal(48, summary.Updated);
        Assert.Equal(1, summary.MondayFailed);
        Assert.Equal(0, summary.VerificationFailed);
        Assert.Equal(3, scenario.Monday.StatusMutations.Count(mutation => mutation.ItemId == itemId));
        Assert.Equal("RETRY_EXHAUSTED_IP_RATE_LIMIT_EXCEEDED", Assert.Single(summary.MondayFailures).ReasonCode);
    }

    [Fact]
    public async Task SuccessfulAcknowledgement_ReadBackMismatchIsNeverUpdated()
    {
        var itemId = HearingStatusRepairService.GetTargets()[0].MondayItemId;
        await using var scenario = await CreateScenarioAsync(
            suppressMutationPersistence: new HashSet<long> { itemId });

        var summary = await scenario.Service.RunAsync(HearingStatusRepairMode.Live, CancellationToken.None);

        Assert.Equal(48, summary.Updated);
        Assert.Equal(0, summary.MondayFailed);
        Assert.Equal(1, summary.VerificationFailed);
        var failure = Assert.Single(summary.VerificationFailures);
        Assert.Equal(itemId, failure.MondayItemId);
        Assert.Equal("READBACK_MISMATCH", failure.ReasonCode);
    }

    [Fact]
    public async Task PartialSuccess_AccountsForCorrectFailureAndMismatchSeparately()
    {
        var targets = HearingStatusRepairService.GetTargets();
        var alreadyCorrect = targets[0];
        var mutationFailure = targets[1];
        var verificationFailure = targets[2];
        await using var scenario = await CreateScenarioAsync(
            currentLabels: new Dictionary<long, string?>
            {
                [alreadyCorrect.MondayItemId] = alreadyCorrect.ExpectedLabel
            },
            failedMutationItemId: mutationFailure.MondayItemId,
            suppressMutationPersistence: new HashSet<long> { verificationFailure.MondayItemId });

        var summary = await scenario.Service.RunAsync(HearingStatusRepairMode.Live, CancellationToken.None);

        Assert.Equal(1, summary.AlreadyCorrect);
        Assert.Equal(48, summary.Planned);
        Assert.Equal(46, summary.Updated);
        Assert.Equal(1, summary.MondayFailed);
        Assert.Equal(1, summary.VerificationFailed);
        Assert.Equal(49, summary.AlreadyCorrect + summary.Updated + summary.MondayFailed + summary.VerificationFailed);
    }

    private static async Task<Scenario> CreateScenarioAsync(
        IReadOnlyDictionary<long, string?>? currentLabels = null,
        IReadOnlyDictionary<long, int>? statusOverrides = null,
        IReadOnlySet<long>? inactiveItemIds = null,
        IEnumerable<string>? allowedLabels = null,
        long? failedMutationItemId = null,
        IReadOnlyDictionary<long, Queue<Exception>>? mutationFailures = null,
        IReadOnlySet<long>? suppressMutationPersistence = null)
    {
        var options = new DbContextOptionsBuilder<IntegrationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;
        var db = new IntegrationDbContext(options);
        var targets = HearingStatusRepairService.GetTargets();
        var mappings = targets.Select((target, index) => new MondayItemMapping
        {
            Id = index + 1,
            BoardId = HearingStatusRepairService.TargetBoardId,
            MondayItemId = target.MondayItemId,
            TikCounter = 100000 + index,
            TikNumber = $"SYNTH-{index:000}",
            CreatedAtUtc = DateTime.UnixEpoch
        }).ToArray();
        db.MondayItemMappings.AddRange(mappings);
        await db.SaveChangesAsync();

        var reader = new FakeOdcanitReader(mappings, targets, statusOverrides, NowUtc);
        var monday = new FakeMondayClient(
            currentLabels,
            inactiveItemIds,
            failedMutationItemId,
            mutationFailures,
            suppressMutationPersistence);
        var delay = new FakeDelay();
        var metadata = new FakeMetadataProvider(allowedLabels ??
        [
            HearingStatusRepairService.CancelledLabel,
            HearingStatusRepairService.TransferredLabel
        ]);
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["HearingNearest:RecoveryLookbackDays"] = "30"
        }).Build();
        var mappingReader = new MondayMappingReadService(
            db,
            NullLogger<MondayMappingReadService>.Instance);
        var service = new HearingStatusRepairService(
            mappingReader,
            reader,
            monday,
            metadata,
            config,
            new FixedTimeProvider(NowUtc),
            delay);
        return new Scenario(db, service, monday, delay);
    }

    private sealed class Scenario(
        IntegrationDbContext db,
        HearingStatusRepairService service,
        FakeMondayClient monday,
        FakeDelay delay) : IAsyncDisposable
    {
        public IntegrationDbContext Db { get; } = db;
        public HearingStatusRepairService Service { get; } = service;
        public FakeMondayClient Monday { get; } = monday;
        public FakeDelay Delay { get; } = delay;
        public ValueTask DisposeAsync() => Db.DisposeAsync();
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    internal sealed class FakeDelay : IHearingStatusRepairDelay
    {
        public List<TimeSpan> Delays { get; } = new();
        public Task DelayAsync(TimeSpan delay, CancellationToken ct)
        {
            Delays.Add(delay);
            return Task.CompletedTask;
        }
    }

    private sealed class FakeOdcanitReader : IOdcanitReader
    {
        private readonly IReadOnlyList<MondayItemMapping> _mappings;
        private readonly IReadOnlyList<HearingStatusRepairTarget> _targets;
        private readonly IReadOnlyDictionary<long, int> _statusOverrides;
        private readonly DateTimeOffset _nowUtc;

        public FakeOdcanitReader(
            IReadOnlyList<MondayItemMapping> mappings,
            IReadOnlyList<HearingStatusRepairTarget> targets,
            IReadOnlyDictionary<long, int>? statusOverrides,
            DateTimeOffset nowUtc)
        {
            _mappings = mappings;
            _targets = targets;
            _statusOverrides = statusOverrides ?? new Dictionary<long, int>();
            _nowUtc = nowUtc;
        }

        public Task<Dictionary<string, TikNumberResolution>> ResolveTikNumbersWithAmbiguityAsync(IEnumerable<string> tikNumbers, CancellationToken ct)
            => Task.FromResult(_mappings.ToDictionary(
                mapping => mapping.TikNumber!,
                mapping => new TikNumberResolution(mapping.TikCounter, false),
                StringComparer.Ordinal));

        public Task<List<OdcanitDiaryEvent>> GetDiaryEventsByTikCountersAsync(IEnumerable<int> tikCounters, CancellationToken ct)
        {
            var requested = tikCounters.ToHashSet();
            var rows = _mappings.Zip(_targets).Where(pair => requested.Contains(pair.First.TikCounter))
                .Select(pair => new OdcanitDiaryEvent
                {
                    TikCounter = pair.First.TikCounter,
                    StartDate = _nowUtc.UtcDateTime.AddDays(1),
                    MeetStatus = _statusOverrides.TryGetValue(pair.Second.MondayItemId, out var status)
                        ? status
                        : pair.Second.ExpectedMeetStatus
                }).ToList();
            return Task.FromResult(rows);
        }

        public Task<List<OdcanitCase>> GetCasesCreatedOnDateAsync(DateTime date, CancellationToken ct) => Task.FromResult(new List<OdcanitCase>());
        public Task<List<OdcanitCase>> GetCasesByTikCountersAsync(IEnumerable<int> tikCounters, CancellationToken ct) => Task.FromResult(new List<OdcanitCase>());
        public Task<Dictionary<string, int>> ResolveTikNumbersToCountersAsync(IEnumerable<string> tikNumbers, CancellationToken ct) => Task.FromResult(new Dictionary<string, int>());
        public Task<List<int>> GetTikCountersSinceCutoffAsync(DateTime cutoffDate, CancellationToken ct) => Task.FromResult(new List<int>());
    }

    private sealed class FakeMondayClient : IMondayClient
    {
        private readonly Dictionary<long, string?> _currentLabels;
        private readonly IReadOnlySet<long> _inactiveItemIds;
        private readonly long? _failedMutationItemId;
        private readonly IReadOnlyDictionary<long, Queue<Exception>> _mutationFailures;
        private readonly IReadOnlySet<long> _suppressMutationPersistence;

        public FakeMondayClient(
            IReadOnlyDictionary<long, string?>? currentLabels,
            IReadOnlySet<long>? inactiveItemIds,
            long? failedMutationItemId,
            IReadOnlyDictionary<long, Queue<Exception>>? mutationFailures,
            IReadOnlySet<long>? suppressMutationPersistence)
        {
            _currentLabels = currentLabels?.ToDictionary(pair => pair.Key, pair => pair.Value) ?? new();
            _inactiveItemIds = inactiveItemIds ?? new HashSet<long>();
            _failedMutationItemId = failedMutationItemId;
            _mutationFailures = mutationFailures ?? new Dictionary<long, Queue<Exception>>();
            _suppressMutationPersistence = suppressMutationPersistence ?? new HashSet<long>();
        }

        public List<(long BoardId, long ItemId, string Label, string ColumnId)> StatusMutations { get; } = new();
        public int OtherMutationCount { get; private set; }

        public Task<MondayItemStatusValue?> GetItemStatusValueAsync(long boardId, long itemId, string statusColumnId, CancellationToken ct)
            => Task.FromResult<MondayItemStatusValue?>(new(
                boardId,
                itemId,
                _inactiveItemIds.Contains(itemId) ? "archived" : "active",
                _currentLabels.GetValueOrDefault(itemId)));

        public Task UpdateHearingStatusAsync(long boardId, long itemId, string label, string statusColumnId, CancellationToken ct)
        {
            StatusMutations.Add((boardId, itemId, label, statusColumnId));
            if (_mutationFailures.TryGetValue(itemId, out var failures) && failures.Count > 0)
            {
                throw failures.Dequeue();
            }
            if (_failedMutationItemId == itemId)
            {
                throw new InvalidOperationException("Synthetic mutation failure.");
            }
            if (!_suppressMutationPersistence.Contains(itemId))
            {
                _currentLabels[itemId] = label;
            }
            return Task.CompletedTask;
        }

        public Task UpdateHearingDetailsAsync(long boardId, long itemId, string judgeName, string city, string judgeColumnId, string cityColumnId, CancellationToken ct) { OtherMutationCount++; return Task.CompletedTask; }
        public Task UpdateHearingDateAsync(long boardId, long itemId, DateTime startDate, string dateColumnId, string hourColumnId, CancellationToken ct) { OtherMutationCount++; return Task.CompletedTask; }
        public Task UpdateItemAsync(long boardId, long itemId, string columnValuesJson, CancellationToken ct) { OtherMutationCount++; return Task.CompletedTask; }
        public Task UpdateItemNameAsync(long boardId, long itemId, string name, CancellationToken ct) { OtherMutationCount++; return Task.CompletedTask; }
        public Task<long> CreateItemAsync(long boardId, string groupId, string itemName, string columnValuesJson, CancellationToken ct) { OtherMutationCount++; return Task.FromResult(0L); }
        public Task<IReadOnlyList<string>> GetBoardGroupIdsAsync(long boardId, CancellationToken ct) => Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());
        public Task<string?> GetItemStateAsync(long boardId, long itemId, CancellationToken ct) => Task.FromResult<string?>("active");
        public Task<long?> FindItemIdByColumnValueAsync(long boardId, string columnId, string columnValue, CancellationToken ct) => Task.FromResult<long?>(null);
        public Task<string?> GetHearingApprovalStatusAsync(long itemId, CancellationToken ct) => Task.FromResult<string?>(null);
    }

    private sealed class FakeMetadataProvider(IEnumerable<string> allowedLabels) : IMondayMetadataProvider
    {
        private readonly HashSet<string> _labels = new(allowedLabels, StringComparer.Ordinal);
        public Task<Dictionary<string, BoardColumnMetadata>> GetBoardColumnsMetadataAsync(long boardId, CancellationToken ct = default)
            => Task.FromResult(new Dictionary<string, BoardColumnMetadata>(StringComparer.Ordinal)
            {
                [HearingStatusRepairService.StatusColumnId] = new()
                {
                    ColumnId = HearingStatusRepairService.StatusColumnId,
                    ColumnType = "color"
                }
            });
        public Task<HashSet<string>> GetAllowedStatusLabelsAsync(long boardId, string columnId, CancellationToken ct = default) => Task.FromResult(new HashSet<string>(_labels, StringComparer.Ordinal));
        public Task<HashSet<string>> GetAllowedDropdownLabelsAsync(long boardId, string columnId, CancellationToken ct = default) => Task.FromResult(new HashSet<string>());
        public Task<string> GetColumnIdByTitleAsync(long boardId, string title, CancellationToken ct = default) => Task.FromResult(string.Empty);
        public Task<string?> GetColumnTypeAsync(long boardId, string columnId, CancellationToken ct = default) => Task.FromResult<string?>("color");
    }
}
