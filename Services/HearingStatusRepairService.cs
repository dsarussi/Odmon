using Microsoft.Extensions.Configuration;
using Odmon.Worker.Data;
using Odmon.Worker.Monday;
using Odmon.Worker.Models;
using Odmon.Worker.OdcanitAccess;

namespace Odmon.Worker.Services;

internal enum HearingStatusRepairMode
{
    DryRun = 0,
    Live = 1
}

internal sealed record HearingStatusRepairTarget(long MondayItemId, int ExpectedMeetStatus)
{
    public string ExpectedLabel => ExpectedMeetStatus switch
    {
        1 => HearingStatusRepairService.CancelledLabel,
        2 => HearingStatusRepairService.TransferredLabel,
        _ => throw new InvalidOperationException("Unsupported repair status.")
    };
}

internal sealed record HearingStatusRepairSummary(
    int Allowlisted,
    int SourceValidated,
    int Planned,
    int AlreadyCorrect,
    int Updated,
    int ValidationFailed,
    int MondayFailed,
    int VerificationFailed,
    int SnapshotStatusRecorded,
    int SnapshotUnchanged,
    IReadOnlyList<HearingStatusRepairItemFailure> ValidationFailures,
    IReadOnlyList<HearingStatusRepairItemFailure> MondayFailures,
    IReadOnlyList<HearingStatusRepairItemFailure> VerificationFailures);

internal sealed record HearingStatusRepairItemFailure(long MondayItemId, string ReasonCode);

internal interface IHearingStatusRepairDelay
{
    Task DelayAsync(TimeSpan delay, CancellationToken ct);
}

internal sealed class HearingStatusRepairDelay : IHearingStatusRepairDelay
{
    public Task DelayAsync(TimeSpan delay, CancellationToken ct) => Task.Delay(delay, ct);
}

/// <summary>
/// One-off, allow-listed repair for the hearing status column only. This service
/// never writes IntegrationDb and deliberately leaves full hearing snapshots
/// unchanged because their date/judge/city fields are outside this repair.
/// </summary>
internal sealed class HearingStatusRepairService
{
    internal const long TargetBoardId = 5035534500;
    internal const string StatusColumnId = "color_mkzqbrta";
    internal const string CancelledLabel = "מבוטל";
    internal const string TransferredLabel = "הועבר";
    private const int MutationAttempts = 3;
    private const int VerificationAttempts = 3;
    private static readonly TimeSpan MutationInterval = TimeSpan.FromSeconds(2);

    private static readonly TimeZoneInfo IsraelTimeZone =
        TimeZoneInfo.FindSystemTimeZoneById("Israel Standard Time");

    private static readonly IReadOnlyList<HearingStatusRepairTarget> Targets =
    [
        new(3180270711, 2),
        new(3180274338, 2),
        new(3180275204, 2),
        new(3180280755, 2),
        new(2923339360, 2),
        new(2923395263, 1),
        new(3180278628, 1),
        new(3180275133, 1),
        new(3180288794, 1),
        new(3180273823, 1),
        new(3180270620, 1),
        new(3180275250, 1),
        new(3180270501, 1),
        new(3180275460, 1),
        new(3180278729, 1),
        new(3180275197, 1),
        new(3180274335, 1),
        new(3180274458, 1),
        new(3180270669, 1),
        new(3180284782, 1),
        new(3180284785, 1),
        new(2923394984, 1),
        new(3180280019, 1),
        new(3180272325, 1),
        new(3180280538, 1),
        new(3180281064, 1),
        new(3180274600, 1),
        new(3180289419, 1),
        new(3180280517, 1),
        new(3180289246, 1),
        new(3180281588, 1),
        new(3180282927, 1),
        new(3180276023, 1),
        new(3180290838, 1),
        new(3180281624, 1),
        new(3180290220, 1),
        new(3180276515, 1),
        new(2766476048, 1),
        new(2766467036, 1),
        new(3180274996, 1),
        new(3195415461, 1),
        new(3195422609, 1),
        new(3195412390, 1),
        new(3180275069, 1),
        new(2835570885, 1),
        new(3195437801, 1),
        new(2766459225, 1),
        new(2923392720, 1),
        new(3195425414, 1)
    ];

    private readonly MondayMappingReadService _mappingReader;
    private readonly IOdcanitReader _odcanitReader;
    private readonly IMondayClient _mondayClient;
    private readonly IMondayMetadataProvider _metadataProvider;
    private readonly IConfiguration _configuration;
    private readonly TimeProvider _timeProvider;
    private readonly IHearingStatusRepairDelay _delay;

    public HearingStatusRepairService(
        MondayMappingReadService mappingReader,
        IOdcanitReader odcanitReader,
        IMondayClient mondayClient,
        IMondayMetadataProvider metadataProvider,
        IConfiguration configuration,
        TimeProvider timeProvider,
        IHearingStatusRepairDelay delay)
    {
        _mappingReader = mappingReader;
        _odcanitReader = odcanitReader;
        _mondayClient = mondayClient;
        _metadataProvider = metadataProvider;
        _configuration = configuration;
        _timeProvider = timeProvider;
        _delay = delay;
    }

    internal static IReadOnlyList<HearingStatusRepairTarget> GetTargets() => Targets;

    internal static void ValidateAllowList(IReadOnlyCollection<HearingStatusRepairTarget> targets)
    {
        var uniqueCount = targets.Select(target => target.MondayItemId).Distinct().Count();
        var cancelledCount = targets.Count(target => target.ExpectedMeetStatus == 1);
        var transferredCount = targets.Count(target => target.ExpectedMeetStatus == 2);
        var unsupportedCount = targets.Count(target => target.ExpectedMeetStatus is not (1 or 2));
        var overlap = targets
            .GroupBy(target => target.MondayItemId)
            .Any(group => group.Select(target => target.ExpectedMeetStatus).Distinct().Count() > 1);

        if (targets.Count != 49 || uniqueCount != 49 || cancelledCount != 44 ||
            transferredCount != 5 || unsupportedCount != 0 || overlap)
        {
            throw new InvalidOperationException(
                "Hearing status repair allow-list invariant failed; no mutation is permitted.");
        }
    }

    public async Task<HearingStatusRepairSummary> RunAsync(
        HearingStatusRepairMode mode,
        CancellationToken ct)
    {
        if (!Enum.IsDefined(mode))
        {
            throw new InvalidOperationException("Hearing status repair mode is invalid.");
        }

        ValidateAllowList(Targets);
        await ValidateMondayMetadataAsync(ct);

        var mappings = await _mappingReader.GetAllByBoardReadOnlyAsync(TargetBoardId, ct);
        var mappingsByItem = mappings
            .Where(mapping => Targets.Any(target => target.MondayItemId == mapping.MondayItemId))
            .GroupBy(mapping => mapping.MondayItemId)
            .ToDictionary(group => group.Key, group => group.ToList());

        var failures = new Dictionary<long, string>();
        var validMappings = new Dictionary<long, MondayItemMapping>();
        foreach (var target in Targets)
        {
            if (!mappingsByItem.TryGetValue(target.MondayItemId, out var matches) ||
                matches.Count != 1)
            {
                failures.TryAdd(target.MondayItemId, "mapping_missing_or_ambiguous");
                continue;
            }

            var mapping = matches[0];
            if (mapping.BoardId != TargetBoardId || mapping.MondayItemId <= 0 ||
                mapping.TikCounter <= 0 || string.IsNullOrWhiteSpace(mapping.TikNumber))
            {
                failures.TryAdd(target.MondayItemId, "mapping_invalid");
                continue;
            }

            validMappings[target.MondayItemId] = mapping;
        }

        var ambiguousMappingItems = validMappings
            .GroupBy(pair => pair.Value.TikCounter)
            .Where(group => group.Count() > 1)
            .SelectMany(group => group.Select(pair => pair.Key))
            .Concat(validMappings
                .GroupBy(pair => pair.Value.TikNumber!.Trim(), StringComparer.Ordinal)
                .Where(group => group.Count() > 1)
                .SelectMany(group => group.Select(pair => pair.Key)))
            .Distinct()
            .ToArray();
        foreach (var itemId in ambiguousMappingItems)
        {
            failures.TryAdd(itemId, "mapping_duplicated_across_allowlist");
            validMappings.Remove(itemId);
        }

        var tikNumbers = validMappings.Values
            .Select(mapping => mapping.TikNumber!.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var resolutions = await _odcanitReader.ResolveTikNumbersWithAmbiguityAsync(tikNumbers, ct);
        foreach (var pair in validMappings.ToArray())
        {
            var mapping = pair.Value;
            if (!resolutions.TryGetValue(mapping.TikNumber!.Trim(), out var resolution) ||
                !resolution.IsResolved || resolution.TikCounter != mapping.TikCounter)
            {
                failures.TryAdd(pair.Key, resolution?.IsAmbiguous == true
                    ? "mapping_identity_ambiguous"
                    : "mapping_identity_unresolved_or_mismatched");
                validMappings.Remove(pair.Key);
            }
        }

        var counters = validMappings.Values.Select(mapping => mapping.TikCounter).Distinct().ToArray();
        var diaryRows = await _odcanitReader.GetDiaryEventsByTikCountersAsync(counters, ct);
        var nowLocal = TimeZoneInfo.ConvertTimeFromUtc(_timeProvider.GetUtcNow().UtcDateTime, IsraelTimeZone);
        var recoveryLookbackDays = Math.Clamp(
            _configuration.GetValue<int>("HearingNearest:RecoveryLookbackDays", 30),
            0,
            365);
        var selectedByTik = HearingSelector.PickNearestUpcomingHearing(
            diaryRows,
            nowLocal,
            TimeSpan.FromDays(recoveryLookbackDays));

        var sourceValidated = 0;
        var alreadyCorrect = 0;
        var planned = new List<HearingStatusRepairTarget>();
        foreach (var target in Targets)
        {
            ct.ThrowIfCancellationRequested();
            if (!validMappings.TryGetValue(target.MondayItemId, out var mapping) ||
                !selectedByTik.TryGetValue(mapping.TikCounter, out var selected) ||
                selected.MeetStatus != target.ExpectedMeetStatus)
            {
                failures.TryAdd(target.MondayItemId, "selected_hearing_missing_or_status_changed");
                continue;
            }

            sourceValidated++;
            try
            {
                var liveValue = await _mondayClient.GetItemStatusValueAsync(
                    TargetBoardId,
                    target.MondayItemId,
                    StatusColumnId,
                    ct);
                if (liveValue == null ||
                    liveValue.BoardId != TargetBoardId ||
                    liveValue.ItemId != target.MondayItemId ||
                    !string.Equals(liveValue.State, "active", StringComparison.OrdinalIgnoreCase))
                {
                    failures.TryAdd(target.MondayItemId, "monday_item_missing_inactive_or_identity_mismatch");
                    continue;
                }

                if (string.Equals(liveValue.Label, target.ExpectedLabel, StringComparison.Ordinal))
                {
                    alreadyCorrect++;
                    continue;
                }

                planned.Add(target);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                failures.TryAdd(target.MondayItemId, "monday_status_read_failed");
            }
        }

        var updated = 0;
        var mondayFailed = 0;
        var verificationFailed = 0;
        var mondayFailures = new List<HearingStatusRepairItemFailure>();
        var verificationFailures = new List<HearingStatusRepairItemFailure>();
        if (mode == HearingStatusRepairMode.Live)
        {
            for (var index = 0; index < planned.Count; index++)
            {
                var target = planned[index];
                ct.ThrowIfCancellationRequested();
                if (index > 0)
                {
                    await _delay.DelayAsync(MutationInterval, ct);
                }

                var mutationFailure = await MutateWithRetryAsync(target, ct);
                if (mutationFailure != null)
                {
                    mondayFailed++;
                    mondayFailures.Add(new HearingStatusRepairItemFailure(
                        target.MondayItemId,
                        mutationFailure));
                    continue;
                }

                var verificationFailure = await VerifyMutationAsync(target, ct);
                if (verificationFailure == null)
                {
                    updated++;
                }
                else
                {
                    verificationFailed++;
                    verificationFailures.Add(new HearingStatusRepairItemFailure(
                        target.MondayItemId,
                        verificationFailure));
                }
            }
        }

        return new HearingStatusRepairSummary(
            Allowlisted: Targets.Count,
            SourceValidated: sourceValidated,
            Planned: planned.Count,
            AlreadyCorrect: alreadyCorrect,
            Updated: updated,
            ValidationFailed: failures.Count,
            MondayFailed: mondayFailed,
            VerificationFailed: verificationFailed,
            SnapshotStatusRecorded: 0,
            SnapshotUnchanged: Targets.Count,
            ValidationFailures: failures
                .OrderBy(pair => pair.Key)
                .Select(pair => new HearingStatusRepairItemFailure(pair.Key, pair.Value))
                .ToArray(),
            MondayFailures: mondayFailures,
            VerificationFailures: verificationFailures);
    }

    private async Task<string?> MutateWithRetryAsync(
        HearingStatusRepairTarget target,
        CancellationToken ct)
    {
        for (var attempt = 1; attempt <= MutationAttempts; attempt++)
        {
            try
            {
                await _mondayClient.UpdateHearingStatusAsync(
                    TargetBoardId,
                    target.MondayItemId,
                    target.ExpectedLabel,
                    StatusColumnId,
                    ct);
                return null;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (MondayApiException ex) when (ex.IsRetryableRateLimit() && attempt < MutationAttempts)
            {
                await _delay.DelayAsync(GetRetryDelay(ex, attempt), ct);
            }
            catch (MondayApiException ex)
            {
                return ex.IsRetryableRateLimit()
                    ? $"RETRY_EXHAUSTED_{ex.ErrorCode}"
                    : ex.ErrorCode;
            }
            catch
            {
                return "MUTATION_EXCEPTION";
            }
        }

        return "RETRY_EXHAUSTED";
    }

    private async Task<string?> VerifyMutationAsync(
        HearingStatusRepairTarget target,
        CancellationToken ct)
    {
        var failureCode = "READBACK_MISMATCH";
        for (var attempt = 1; attempt <= VerificationAttempts; attempt++)
        {
            try
            {
                var liveValue = await _mondayClient.GetItemStatusValueAsync(
                    TargetBoardId,
                    target.MondayItemId,
                    StatusColumnId,
                    ct);
                if (liveValue != null &&
                    liveValue.BoardId == TargetBoardId &&
                    liveValue.ItemId == target.MondayItemId &&
                    string.Equals(liveValue.State, "active", StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(liveValue.Label, target.ExpectedLabel, StringComparison.Ordinal))
                {
                    return null;
                }

                failureCode = liveValue == null ||
                              liveValue.BoardId != TargetBoardId ||
                              liveValue.ItemId != target.MondayItemId ||
                              !string.Equals(liveValue.State, "active", StringComparison.OrdinalIgnoreCase)
                    ? "READBACK_ITEM_INVALID"
                    : "READBACK_MISMATCH";
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (MondayApiException ex)
            {
                failureCode = $"READBACK_{ex.ErrorCode}";
            }
            catch
            {
                failureCode = "READBACK_EXCEPTION";
            }

            if (attempt < VerificationAttempts)
            {
                await _delay.DelayAsync(TimeSpan.FromSeconds(attempt), ct);
            }
        }

        return failureCode;
    }

    private static TimeSpan GetRetryDelay(MondayApiException exception, int attempt)
    {
        var requestedSeconds = exception.RetryAfter?.TotalSeconds ?? Math.Pow(2, attempt);
        return TimeSpan.FromSeconds(Math.Clamp(requestedSeconds, 1, 60));
    }

    private async Task ValidateMondayMetadataAsync(CancellationToken ct)
    {
        var columns = await _metadataProvider.GetBoardColumnsMetadataAsync(TargetBoardId, ct);
        if (!columns.ContainsKey(StatusColumnId))
        {
            throw new InvalidOperationException(
                "Hearing status repair preflight failed: required Monday column is missing.");
        }

        var labels = await _metadataProvider.GetAllowedStatusLabelsAsync(
            TargetBoardId,
            StatusColumnId,
            ct);
        if (!labels.Contains(CancelledLabel) || !labels.Contains(TransferredLabel))
        {
            throw new InvalidOperationException(
                "Hearing status repair preflight failed: required Monday labels are missing.");
        }
    }
}
