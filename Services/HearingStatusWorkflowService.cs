using Odmon.Worker.Monday;

namespace Odmon.Worker.Services;

public enum HearingStatusWorkflowOutcome
{
    ActiveNoMutation = 0,
    AlreadyCorrect = 1,
    ProtectedWorkflowStatus = 2,
    Planned = 3,
    Updated = 4,
    AdvancedAfterMutation = 5,
    ValidationFailed = 6,
    MondayFailed = 7,
    VerificationFailed = 8
}

public sealed record HearingStatusWorkflowResult(
    HearingStatusWorkflowOutcome Outcome,
    string? DesiredLabel,
    string? ReasonCode = null)
{
    public bool IsSuccessful => Outcome is
        HearingStatusWorkflowOutcome.ActiveNoMutation or
        HearingStatusWorkflowOutcome.AlreadyCorrect or
        HearingStatusWorkflowOutcome.ProtectedWorkflowStatus or
        HearingStatusWorkflowOutcome.Updated or
        HearingStatusWorkflowOutcome.AdvancedAfterMutation;
}

public interface IHearingStatusDelay
{
    Task DelayAsync(TimeSpan delay, CancellationToken ct);
}

public sealed class HearingStatusDelay : IHearingStatusDelay
{
    public Task DelayAsync(TimeSpan delay, CancellationToken ct) => Task.Delay(delay, ct);
}

/// <summary>
/// Reconciles the source hearing status into the initial portion of Monday's
/// workflow without overwriting downstream workflow states.
/// </summary>
public sealed class HearingStatusWorkflowService
{
    public const string ActiveBaselineLabel = "דיון לא בוטל";
    public const string CancelledLabel = "מבוטל";
    public const string TransferredLabel = "הועבר";

    private const int MutationAttempts = 3;
    private const int VerificationAttempts = 3;
    private static readonly TimeSpan MutationInterval = TimeSpan.FromSeconds(2);
    private static readonly HashSet<string> ManagedLabels = new(StringComparer.Ordinal)
    {
        ActiveBaselineLabel,
        CancelledLabel,
        TransferredLabel
    };

    private readonly IMondayClient _mondayClient;
    private readonly IHearingStatusDelay _delay;

    public HearingStatusWorkflowService(
        IMondayClient mondayClient,
        IHearingStatusDelay delay)
    {
        _mondayClient = mondayClient;
        _delay = delay;
    }

    public static IReadOnlyCollection<string> RequiredManagedLabels => ManagedLabels;

    public static string? GetDesiredLabel(int meetStatus) => meetStatus switch
    {
        1 => CancelledLabel,
        2 => TransferredLabel,
        _ => null
    };

    public static void ValidateManagedLabels(IReadOnlySet<string> allowedLabels)
    {
        var missing = ManagedLabels.Where(label => !allowedLabels.Contains(label)).ToArray();
        if (missing.Length > 0)
        {
            throw new InvalidOperationException(
                "Hearing status workflow preflight failed: one or more required managed labels are missing.");
        }
    }

    public async Task<HearingStatusWorkflowResult> ReconcileAsync(
        long boardId,
        long itemId,
        string statusColumnId,
        int meetStatus,
        bool live,
        CancellationToken ct)
    {
        var desiredLabel = GetDesiredLabel(meetStatus);
        var initialRead = await ReadStatusAsync(boardId, itemId, statusColumnId, ct);
        if (initialRead.FailureCode != null)
        {
            return new HearingStatusWorkflowResult(
                HearingStatusWorkflowOutcome.ValidationFailed,
                desiredLabel,
                initialRead.FailureCode);
        }

        var currentLabel = NormalizeLabel(initialRead.Value!.Label);

        // Even an active source hearing is validated against the live item first,
        // but it never writes or resets Monday's workflow status. Protected
        // downstream states remain visible in reconciliation accounting.
        if (desiredLabel == null)
        {
            return new HearingStatusWorkflowResult(
                IsProtectedWorkflowLabel(currentLabel)
                    ? HearingStatusWorkflowOutcome.ProtectedWorkflowStatus
                    : HearingStatusWorkflowOutcome.ActiveNoMutation,
                DesiredLabel: null);
        }

        if (string.Equals(currentLabel, desiredLabel, StringComparison.Ordinal))
        {
            return new HearingStatusWorkflowResult(
                HearingStatusWorkflowOutcome.AlreadyCorrect,
                desiredLabel);
        }

        if (IsProtectedWorkflowLabel(currentLabel))
        {
            return new HearingStatusWorkflowResult(
                HearingStatusWorkflowOutcome.ProtectedWorkflowStatus,
                desiredLabel);
        }

        if (!live)
        {
            return new HearingStatusWorkflowResult(
                HearingStatusWorkflowOutcome.Planned,
                desiredLabel);
        }

        await _delay.DelayAsync(MutationInterval, ct);
        var mutationFailure = await MutateWithRetryAsync(
            boardId,
            itemId,
            statusColumnId,
            desiredLabel,
            ct);
        if (mutationFailure != null)
        {
            return new HearingStatusWorkflowResult(
                HearingStatusWorkflowOutcome.MondayFailed,
                desiredLabel,
                mutationFailure);
        }

        return await VerifyMutationAsync(
            boardId,
            itemId,
            statusColumnId,
            desiredLabel,
            ct);
    }

    private async Task<string?> MutateWithRetryAsync(
        long boardId,
        long itemId,
        string statusColumnId,
        string desiredLabel,
        CancellationToken ct)
    {
        for (var attempt = 1; attempt <= MutationAttempts; attempt++)
        {
            try
            {
                await _mondayClient.UpdateHearingStatusAsync(
                    boardId,
                    itemId,
                    desiredLabel,
                    statusColumnId,
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

    private async Task<HearingStatusWorkflowResult> VerifyMutationAsync(
        long boardId,
        long itemId,
        string statusColumnId,
        string desiredLabel,
        CancellationToken ct)
    {
        var failureCode = "READBACK_MISMATCH";
        for (var attempt = 1; attempt <= VerificationAttempts; attempt++)
        {
            var read = await ReadStatusAsync(boardId, itemId, statusColumnId, ct);
            if (read.FailureCode == null)
            {
                var label = NormalizeLabel(read.Value!.Label);
                if (string.Equals(label, desiredLabel, StringComparison.Ordinal))
                {
                    return new HearingStatusWorkflowResult(
                        HearingStatusWorkflowOutcome.Updated,
                        desiredLabel);
                }

                if (IsProtectedWorkflowLabel(label))
                {
                    return new HearingStatusWorkflowResult(
                        HearingStatusWorkflowOutcome.AdvancedAfterMutation,
                        desiredLabel);
                }

                failureCode = "READBACK_MANAGED_MISMATCH";
            }
            else
            {
                failureCode = read.FailureCode.StartsWith("MONDAY_", StringComparison.Ordinal)
                    ? $"READBACK_{read.FailureCode}"
                    : read.FailureCode;
            }

            if (attempt < VerificationAttempts)
            {
                await _delay.DelayAsync(TimeSpan.FromSeconds(attempt), ct);
            }
        }

        return new HearingStatusWorkflowResult(
            HearingStatusWorkflowOutcome.VerificationFailed,
            desiredLabel,
            failureCode);
    }

    private async Task<(MondayItemStatusValue? Value, string? FailureCode)> ReadStatusAsync(
        long boardId,
        long itemId,
        string statusColumnId,
        CancellationToken ct)
    {
        try
        {
            var value = await _mondayClient.GetItemStatusValueAsync(
                boardId,
                itemId,
                statusColumnId,
                ct);
            if (value == null ||
                value.BoardId != boardId ||
                value.ItemId != itemId ||
                !string.Equals(value.State, "active", StringComparison.OrdinalIgnoreCase))
            {
                return (null, "MONDAY_ITEM_INVALID");
            }

            return (value, null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (MondayApiException ex)
        {
            return (null, $"MONDAY_{ex.ErrorCode}");
        }
        catch
        {
            return (null, "MONDAY_STATUS_READ_FAILED");
        }
    }

    private static bool IsProtectedWorkflowLabel(string? label)
        => !string.IsNullOrWhiteSpace(label) && !ManagedLabels.Contains(label);

    private static string? NormalizeLabel(string? label)
        => string.IsNullOrWhiteSpace(label) ? null : label.Trim();

    private static TimeSpan GetRetryDelay(MondayApiException exception, int attempt)
    {
        var requestedSeconds = exception.RetryAfter?.TotalSeconds ?? Math.Pow(2, attempt);
        return TimeSpan.FromSeconds(Math.Clamp(requestedSeconds, 1, 60));
    }
}
