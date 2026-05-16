using System;
using System.Linq;

namespace Odmon.Worker.Configuration
{
    public class HearingBackfillSettings
    {
        public bool Enable { get; set; }

        /// <summary>Qualified source table, e.g. dbo.HearingBackfill_May2026 (same column layout as April/May).</summary>
        public string SourceTable { get; set; } = "dbo.HearingBackfill_May2026";

        public long BoardId { get; set; } = 5035534500;
        public string StatusColumnId { get; set; } = "color_mm12y7zr";
        public int ImportedStatusIndex { get; set; } = 1;
        public int BatchSize { get; set; } = 50;

        /// <summary>
        /// Builds [schema].[table] for raw SQL. Only alphanumeric and underscore segments allowed (no injection).
        /// </summary>
        public static string BuildBracketedQualifiedTable(string? sourceTable)
        {
            var qualified = string.IsNullOrWhiteSpace(sourceTable)
                ? "dbo.HearingBackfill_May2026"
                : sourceTable.Trim();
            var (schema, table) = ParseSchemaAndTable(qualified);
            return $"[{schema}].[{table}]";
        }

        /// <summary>Schema and table name for OBJECT_ID / startup checks.</summary>
        public static (string Schema, string Table) ParseSchemaAndTable(string? sourceTable)
        {
            var qualified = string.IsNullOrWhiteSpace(sourceTable)
                ? "dbo.HearingBackfill_May2026"
                : sourceTable.Trim();

            string schema;
            string table;
            var dot = qualified.IndexOf('.');
            if (dot >= 0)
            {
                schema = qualified[..dot].Trim();
                table = qualified[(dot + 1)..].Trim();
            }
            else
            {
                schema = "dbo";
                table = qualified;
            }

            static bool IsSafeIdent(string id) =>
                id.Length > 0 && id.Length <= 128 &&
                id.All(c => char.IsLetterOrDigit(c) || c == '_');

            if (!IsSafeIdent(schema) || !IsSafeIdent(table))
            {
                throw new InvalidOperationException(
                    $"HearingBackfill:SourceTable must be schema.table with safe identifiers only; got '{qualified}'.");
            }

            return (schema, table);
        }
    }
}
