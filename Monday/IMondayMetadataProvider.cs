namespace Odmon.Worker.Monday
{
    public interface IMondayMetadataProvider
    {
        Task<string> GetColumnIdByTitleAsync(long boardId, string title, CancellationToken ct = default);
        Task<HashSet<string>> GetAllowedDropdownLabelsAsync(long boardId, string columnId, CancellationToken ct = default);
    }
}

