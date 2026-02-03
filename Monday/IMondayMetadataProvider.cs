namespace Odmon.Worker.Monday
{
    public interface IMondayMetadataProvider
    {
        Task<string> GetColumnIdByTitleAsync(long boardId, string title, CancellationToken ct = default);
        Task<HashSet<string>> GetAllowedDropdownLabelsAsync(long boardId, string columnId, CancellationToken ct = default);
        Task<HashSet<string>> GetAllowedStatusLabelsAsync(long boardId, string columnId, CancellationToken ct = default);
        Task<string?> GetColumnTypeAsync(long boardId, string columnId, CancellationToken ct = default);
    }
}

