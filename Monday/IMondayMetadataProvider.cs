using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Odmon.Worker.Monday
{
    public interface IMondayMetadataProvider
    {
        Task<string> GetColumnIdByTitleAsync(long boardId, string title, CancellationToken ct = default);
        Task<HashSet<string>> GetAllowedDropdownLabelsAsync(long boardId, string columnId, CancellationToken ct = default);
        Task<HashSet<string>> GetAllowedStatusLabelsAsync(long boardId, string columnId, CancellationToken ct = default);
        Task<string?> GetColumnTypeAsync(long boardId, string columnId, CancellationToken ct = default);

        /// <summary>
        /// Fetches all column metadata for a board with in-memory caching (10-min TTL).
        /// Returns a dictionary keyed by columnId.
        /// </summary>
        Task<Dictionary<string, BoardColumnMetadata>> GetBoardColumnsMetadataAsync(long boardId, CancellationToken ct = default);
    }

    /// <summary>
    /// Represents metadata for a single column on a Monday board.
    /// </summary>
    public class BoardColumnMetadata
    {
        public string ColumnId { get; set; } = "";
        public string? Title { get; set; }
        public string? ColumnType { get; set; }
        public string? SettingsStr { get; set; }
    }
}
