namespace Odmon.Worker.Monday
{
    public interface IMondayClient
    {
        Task<long> CreateItemAsync(long boardId, string groupId, string itemName, string columnValuesJson, CancellationToken ct);
        Task UpdateItemAsync(long boardId, long itemId, string columnValuesJson, CancellationToken ct);
    }
}

