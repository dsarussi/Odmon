using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Odmon.Worker.Monday
{
    public sealed record MondayItemStatusValue(
        long BoardId,
        long ItemId,
        string State,
        string? Label);

    public sealed record MondayHearingDetailsValue(
        long BoardId,
        long ItemId,
        string State,
        DateOnly? HearingDate,
        TimeOnly? HearingTime,
        string? JudgeName);

    public interface IMondayClient
    {
        /// <summary>Returns group IDs for the board (order preserved). Used for bootstrap group resolution.</summary>
        Task<IReadOnlyList<string>> GetBoardGroupIdsAsync(long boardId, CancellationToken ct);

        Task<long> CreateItemAsync(long boardId, string groupId, string itemName, string columnValuesJson, CancellationToken ct);
        /// <summary>Returns item state (e.g. "active", "archived", "deleted") or null if not found/error.</summary>
        Task<string?> GetItemStateAsync(long boardId, long itemId, CancellationToken ct);
        /// <summary>
        /// Reads one status column together with item identity and lifecycle state.
        /// Returns null only when the requested item does not exist.
        /// </summary>
        Task<MondayItemStatusValue?> GetItemStatusValueAsync(
            long boardId,
            long itemId,
            string statusColumnId,
            CancellationToken ct);
        /// <summary>
        /// Reads the configured hearing date, time, and judge columns together with
        /// item identity and lifecycle state. Returns null only for a successful
        /// empty items result.
        /// </summary>
        Task<MondayHearingDetailsValue?> GetHearingDetailsValueAsync(
            long boardId,
            long itemId,
            string dateColumnId,
            string hourColumnId,
            string judgeColumnId,
            CancellationToken ct);
        Task UpdateItemAsync(long boardId, long itemId, string columnValuesJson, CancellationToken ct);
        Task UpdateItemNameAsync(long boardId, long itemId, string name, CancellationToken ct);
        Task<long?> FindItemIdByColumnValueAsync(long boardId, string columnId, string columnValue, CancellationToken ct);

        // Phase-2 hearing approval: read status from Monday
        Task<string?> GetHearingApprovalStatusAsync(long itemId, CancellationToken ct);

        // Hearing sync: update judge, city, date, hour, status (separate calls for correct WhatsApp trigger ordering)
        Task UpdateHearingDetailsAsync(long boardId, long itemId, string judgeName, string city, string judgeColumnId, string cityColumnId, CancellationToken ct);
        Task UpdateHearingDateAsync(long boardId, long itemId, DateTime startDate, string dateColumnId, string hourColumnId, CancellationToken ct);
        Task UpdateHearingStatusAsync(long boardId, long itemId, string label, string statusColumnId, CancellationToken ct);
    }
}

