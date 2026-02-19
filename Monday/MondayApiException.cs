using System;

namespace Odmon.Worker.Monday
{
    /// <summary>
    /// Exception thrown when Monday.com API returns an error response.
    /// Contains structured error information from the GraphQL API.
    /// </summary>
    public class MondayApiException : InvalidOperationException
    {
        /// <summary>
        /// The raw error JSON from Monday.com API response.
        /// </summary>
        public string? RawErrorJson { get; }

        /// <summary>
        /// The operation that failed (e.g., "create_item", "change_multiple_column_values").
        /// </summary>
        public string? Operation { get; }

        /// <summary>
        /// The board ID involved in the operation (if applicable).
        /// </summary>
        public long? BoardId { get; }

        /// <summary>
        /// The item ID involved in the operation (if applicable).
        /// </summary>
        public long? ItemId { get; }

        /// <summary>
        /// A snippet of the column values JSON that was sent (for debugging, truncated if too long).
        /// </summary>
        public string? ColumnValuesSnippet { get; }

        public MondayApiException(string message) : base(message)
        {
        }

        public MondayApiException(string message, Exception innerException) : base(message, innerException)
        {
        }

        public MondayApiException(
            string message,
            string? rawErrorJson = null,
            string? operation = null,
            long? boardId = null,
            long? itemId = null,
            string? columnValuesSnippet = null)
            : base(message)
        {
            RawErrorJson = rawErrorJson;
            Operation = operation;
            BoardId = boardId;
            ItemId = itemId;
            ColumnValuesSnippet = TruncateSnippet(columnValuesSnippet, 500);
        }

        public MondayApiException(
            string message,
            Exception innerException,
            string? rawErrorJson = null,
            string? operation = null,
            long? boardId = null,
            long? itemId = null,
            string? columnValuesSnippet = null)
            : base(message, innerException)
        {
            RawErrorJson = rawErrorJson;
            Operation = operation;
            BoardId = boardId;
            ItemId = itemId;
            ColumnValuesSnippet = TruncateSnippet(columnValuesSnippet, 500);
        }

        /// <summary>
        /// Returns true if the Monday API error indicates the item is inactive (archived/deleted).
        /// Checks both the RawErrorJson and the Message for the "inactiveItems" error code.
        /// </summary>
        public bool IsInactiveItemError()
        {
            // Check raw JSON for error_data.column_validation_error_code == "inactiveItems"
            if (!string.IsNullOrWhiteSpace(RawErrorJson) &&
                RawErrorJson.Contains("inactiveItems", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            // Fallback: check message text
            if (Message != null && Message.Contains("inactive", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return false;
        }

        private static string? TruncateSnippet(string? snippet, int maxLength)
        {
            if (string.IsNullOrWhiteSpace(snippet))
                return snippet;

            if (snippet.Length <= maxLength)
                return snippet;

            return snippet.Substring(0, maxLength) + "... (truncated)";
        }
    }
}

