using System.Security.Cryptography;
using System.Text;
using Odmon.Worker.Configuration;

namespace Odmon.Worker.Services
{
    internal static class AccidentStoryComposer
    {
        /// <summary>
        /// Composes a deterministic text block from multiple questionnaire column values.
        /// Returns null when all column values are empty (and none have IncludeIfEmpty).
        /// When includeHeader is false, Text contains ONLY the Q/A bullet lines (no ItemId, timestamp, or source header).
        /// </summary>
        internal static AccidentStoryComposeResult? Compose(
            AccidentStoryColumnDef[] columnDefs,
            Dictionary<string, string> columnValues,
            long questionnaireItemId,
            DateTime timestamp,
            bool includeHeader = true)
        {
            var lines = new List<string>();

            foreach (var col in columnDefs)
            {
                columnValues.TryGetValue(col.ColumnId, out var rawValue);
                var value = rawValue?.Trim() ?? "";

                if (string.IsNullOrWhiteSpace(value) && !col.IncludeIfEmpty)
                    continue;

                var displayValue = string.IsNullOrWhiteSpace(value) ? "(ריק)" : value;
                lines.Add($"- {col.Title}: {displayValue}");
            }

            if (lines.Count == 0)
                return null;

            string fullText;
            if (includeHeader)
            {
                var header = $"סיפור תאונה (מקור: שאלון Monday, ItemId={questionnaireItemId}, תאריך={timestamp:dd/MM/yyyy HH:mm})";
                fullText = header + "\n" + string.Join("\n", lines);
            }
            else
            {
                fullText = string.Join("\n", lines);
            }

            return new AccidentStoryComposeResult
            {
                Text = fullText,
                LinesIncluded = lines.Count,
                ContentHash = ComputeSha256(fullText)
            };
        }

        internal static string ComputeSha256(string input)
        {
            if (string.IsNullOrEmpty(input))
                return string.Empty;

            var bytes = Encoding.UTF8.GetBytes(input);
            var hash = SHA256.HashData(bytes);
            return Convert.ToHexString(hash).ToLowerInvariant();
        }
    }

    internal class AccidentStoryComposeResult
    {
        public string Text { get; init; } = string.Empty;
        public int LinesIncluded { get; init; }
        public string ContentHash { get; init; } = string.Empty;
    }
}
