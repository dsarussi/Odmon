using System;
using System.Text;

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

        /// <summary>Sanitized Monday/transport error classification.</summary>
        public string ErrorCode { get; }

        public int? HttpStatusCode { get; }

        public TimeSpan? RetryAfter { get; }

        public MondayApiException(string message) : base(message)
        {
            ErrorCode = "MONDAY_API_ERROR";
        }

        public MondayApiException(string message, Exception innerException) : base(message, innerException)
        {
            ErrorCode = "MONDAY_API_ERROR";
        }

        public MondayApiException(
            string message,
            string? rawErrorJson = null,
            string? operation = null,
            long? boardId = null,
            long? itemId = null,
            string? columnValuesSnippet = null,
            string? errorCode = null,
            int? httpStatusCode = null,
            TimeSpan? retryAfter = null)
            : base(message)
        {
            RawErrorJson = rawErrorJson;
            Operation = operation;
            BoardId = boardId;
            ItemId = itemId;
            ColumnValuesSnippet = TruncateSnippet(columnValuesSnippet, 500);
            ErrorCode = NormalizeErrorCode(errorCode);
            HttpStatusCode = httpStatusCode;
            RetryAfter = retryAfter;
        }

        public MondayApiException(
            string message,
            Exception innerException,
            string? rawErrorJson = null,
            string? operation = null,
            long? boardId = null,
            long? itemId = null,
            string? columnValuesSnippet = null,
            string? errorCode = null,
            int? httpStatusCode = null,
            TimeSpan? retryAfter = null)
            : base(message, innerException)
        {
            RawErrorJson = rawErrorJson;
            Operation = operation;
            BoardId = boardId;
            ItemId = itemId;
            ColumnValuesSnippet = TruncateSnippet(columnValuesSnippet, 500);
            ErrorCode = NormalizeErrorCode(errorCode);
            HttpStatusCode = httpStatusCode;
            RetryAfter = retryAfter;
        }

        public bool IsRetryableRateLimit()
        {
            if (string.Equals(ErrorCode, "DAILY_LIMIT_EXCEEDED", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(ErrorCode, "REQUEST_MAX_COMPLEXITY_EXCEEDED", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            return HttpStatusCode == 429 || ErrorCode switch
            {
                "COMPLEXITY_BUDGET_EXHAUSTED" => true,
                "IP_RATE_LIMIT_EXCEEDED" => true,
                "MAX_CONCURRENCY_EXCEEDED" => true,
                "MINUTE_RATE_LIMIT_EXCEEDED" => true,
                "RATE_LIMIT_EXCEEDED" => true,
                _ => false
            };
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

        private static string NormalizeErrorCode(string? errorCode)
        {
            if (string.IsNullOrWhiteSpace(errorCode))
            {
                return "MONDAY_API_ERROR";
            }

            var source = errorCode.Trim().ToUpperInvariant();
            var builder = new StringBuilder(Math.Min(source.Length, 64));
            foreach (var character in source)
            {
                if (builder.Length == 64)
                {
                    break;
                }

                builder.Append(character is >= 'A' and <= 'Z' or >= '0' and <= '9' or '_'
                    ? character
                    : '_');
            }
            var normalized = builder.Length == 0 ? "MONDAY_API_ERROR" : builder.ToString();
            return normalized switch
            {
                "MAXCONCURRENCYEXCEEDED" => "MAX_CONCURRENCY_EXCEEDED",
                "MINUTE_LIMIT_RATE_EXCEEDED" => "MINUTE_RATE_LIMIT_EXCEEDED",
                _ => normalized
            };
        }
    }
}

