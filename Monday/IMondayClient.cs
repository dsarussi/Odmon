using System;
using System.Threading;
using System.Threading.Tasks;

namespace Odmon.Worker.Monday
{
    public interface IMondayClient
    {
        Task<long> CreateItemAsync(long boardId, string groupId, string itemName, string columnValuesJson, CancellationToken ct);
        /// <summary>Returns item state (e.g. "active", "archived", "deleted") or null if not found/error.</summary>
        Task<string?> GetItemStateAsync(long boardId, long itemId, CancellationToken ct);
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

