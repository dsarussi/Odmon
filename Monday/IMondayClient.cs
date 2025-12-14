namespace Odmon.Worker.Monday
{
    public interface IMondayClient
    {
        Task<long> CreateItemAsync(long boardId, string groupId, string itemName, string columnValuesJson, CancellationToken ct);
        Task UpdateItemAsync(long boardId, long itemId, string columnValuesJson, CancellationToken ct);
        Task UpdateItemNameAsync(long boardId, long itemId, string name, CancellationToken ct);
        Task<long?> FindItemIdByColumnValueAsync(long boardId, string columnId, string columnValue, CancellationToken ct);
    }
}

