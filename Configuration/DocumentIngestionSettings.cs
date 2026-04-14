namespace Odmon.Worker.Configuration
{
    /// <summary>
    /// Centralized mapping from Monday file column IDs to Hebrew document type names.
    /// Used to generate deterministic business filenames for Odcanit imports.
    /// </summary>
    public static class DocumentTypeMap
    {
        private static readonly Dictionary<string, string> ColumnMap = new(StringComparer.OrdinalIgnoreCase)
        {
            ["file_mm1bvngc"] = "כתב תביעה",
            ["file_mm0qwtat"] = "תצהיר ויפוי כח",
            ["file_mkzr2cmr"] = "מסמך נלווה",
            ["file_mkyet713"] = "תיעוד ממקום התאונה",
        };

        private static readonly Dictionary<int, string> ClientMap = new()
        {
            [1]   = "כתב הגנה",
            [101] = "כתב הגנה",
            [2]   = "כתב הגנה",
            [23]  = "כתב הגנה",
            [8]   = "כתב הגנה",
            [5]   = "כתב הגנה",
            [452] = "כתב תביעה",
            [371] = "כתב תביעה",
            [104] = "כתב תביעה",
            [253] = "כתב תביעה",
            [275] = "כתב תביעה",
            [18]  = "כתב תביעה",
            [22]  = "כתב תביעה",
            [102] = "כתב תביעה",
            [250] = "כתב תביעה",
            [274] = "כתב תביעה",
            [16]  = "כתב תביעה",
            [340] = "כתב תביעה",
            [163] = "כתב תביעה",
            [482] = "כתב תביעה",
            [484] = "כתב תביעה",
            [502] = "כתב תביעה",
            [513] = "כתב תביעה",
            [6]   = "מכתב דרישה אילי",
        };

        /// <summary>
        /// Extracts the business client number from a visual ID string.
        /// TikVisualID uses '/' (e.g. "9/1990" → 9). ClientVisualID uses '\' (e.g. "102\5334" → 102).
        /// </summary>
        public static int? ParseClientNumber(string? id, char separator)
        {
            if (string.IsNullOrWhiteSpace(id)) return null;
            var sepIndex = id.IndexOf(separator);
            var prefix = sepIndex > 0 ? id.Substring(0, sepIndex).Trim() : id.Trim();
            return int.TryParse(prefix, out var num) ? num : null;
        }

        /// <summary>
        /// Resolves DocumentType using the official client-number mapping with legacy fallback.
        /// Returns (DocumentType, DerivationPath) where DerivationPath is ExplicitMapping, LegacyFallback, or Unresolved.
        /// </summary>
        public static (string? DocumentType, string DerivationPath) ResolveDocumentType(int clientNumber)
        {
            if (ClientMap.TryGetValue(clientNumber, out var explicitType))
                return (explicitType, "ExplicitMapping");

            if (clientNumber is 4 or 7 or 9)
                return ("כתב תביעה", "LegacyFallback");

            if (clientNumber >= 100)
                return ("כתב תביעה", "LegacyFallback");

            return (null, "Unresolved");
        }

        /// <summary>Resolve DocumentType by client number. Returns null if not in explicit mapping.</summary>
        public static string? ResolveByClientNumber(int clientNumber)
            => ClientMap.TryGetValue(clientNumber, out var name) ? name : null;

        /// <summary>Resolve DocumentType from Monday file column ID. Returns "מסמך" if column is unknown.</summary>
        public static string Resolve(string columnId)
            => ColumnMap.TryGetValue(columnId, out var name) ? name : "מסמך";

        /// <summary>
        /// Build a deterministic business filename: "({TikCounter}){TikVisualID} - {DocType}.{ext}"
        /// Invalid filename chars are stripped. "/" in TikVisualID becomes "-".
        /// </summary>
        public static string BuildBusinessFileName(int tikCounter, string tikVisualID, string documentType, string extension)
        {
            var safeTik = tikVisualID.Replace("/", "-").Replace("\\", "-").Trim();
            if (string.IsNullOrEmpty(safeTik)) safeTik = "unknown";
            var ext = (extension ?? ".pdf").TrimStart('.');
            if (string.IsNullOrEmpty(ext)) ext = "pdf";
            ext = ext.ToLowerInvariant();
            var raw = $"({tikCounter}){safeTik} - {documentType}.{ext}";
            var invalid = Path.GetInvalidFileNameChars();
            return new string(raw.Where(c => !invalid.Contains(c) || c == ' ' || c == '-' || c == '.' || c == '(' || c == ')').ToArray()).Trim();
        }
    }

    public class DocumentIngestionSettings
    {
        public bool Enabled { get; set; } = false;
        public long BoardId { get; set; } = 5088708083;
        public long LinkedCasesBoardId { get; set; } = 5035534500;
        public string InboxPath { get; set; } = @"D:\Odlight\OdmonInbox";
        public long MaxFileSizeBytes { get; set; } = 52428800;
        public string[] AllowedExtensions { get; set; } = ["pdf", "jpg", "jpeg", "png"];
        public string[] Columns { get; set; } = ["file_mm0qwtat", "file_mkzr2cmr"];

        /// <summary>Per-column max file size overrides. Key = column ID, value = max bytes. Falls back to <see cref="MaxFileSizeBytes"/> if absent.</summary>
        public Dictionary<string, long> ColumnMaxFileSizeBytes { get; set; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Extensions that are always blocked regardless of allowlist.
        /// Checked against the raw filename from Monday before downloading.
        /// </summary>
        public string[] DeniedExtensions { get; set; } =
            ["exe", "msi", "bat", "cmd", "ps1", "js", "vbs", "scr", "com", "hta", "jar", "zip", "rar", "7z"];

        public long GetMaxFileSizeForColumn(string? columnId)
        {
            if (!string.IsNullOrEmpty(columnId) &&
                ColumnMaxFileSizeBytes.TryGetValue(columnId, out var perColumn) &&
                perColumn > 0)
                return perColumn;
            return MaxFileSizeBytes;
        }

        public bool IsDeniedExtension(string? ext)
        {
            if (string.IsNullOrWhiteSpace(ext)) return false;
            var normalized = ext.TrimStart('.');
            return Array.Exists(DeniedExtensions, d => string.Equals(d, normalized, StringComparison.OrdinalIgnoreCase));
        }
        public string RelationColumnId { get; set; } = "board_relation_mkzenscq";
        public string LinkedCaseTikColumnId { get; set; } = "text_mkwe19hn";
        public int IntervalSeconds { get; set; } = 300;
        public int MaxRetryCount { get; set; } = 3;
        public int CommandTimeoutSeconds { get; set; } = 60;
        public int ItemsPageLimit { get; set; } = 50;

        /// <summary>Delay in ms between processing individual items to reduce DB pressure. 0 = no delay.</summary>
        public int ItemProcessingDelayMs { get; set; } = 300;

        public AccidentStorySettings AccidentStory { get; set; } = new();

        /// <summary>Backward compat: if set and AccidentStory.Columns is empty, treated as single-column config.</summary>
        public string? AccidentStoryColumnId { get; set; }
        public string AccidentStoryNispahType { get; set; } = "סיפור תאונה";

        public bool IsAccidentStoryEnabled =>
            AccidentStory.Enabled ||
            !string.IsNullOrWhiteSpace(AccidentStoryColumnId);

        public string ResolvedNispahType =>
            AccidentStory.Enabled
                ? AccidentStory.NispahType
                : AccidentStoryNispahType;

        /// <summary>Optional Tasks board source (משימות). When enabled, imports Word documents from items with status "טופס נוצר בהצלחה".</summary>
        public TasksBoardSourceSettings? TasksSource { get; set; }
    }

    public class TasksBoardSourceSettings
    {
        public bool Enabled { get; set; } = false;
        public long BoardId { get; set; } = 5035534505;
        public string TaskStatusColumnId { get; set; } = "color_mkwej7ys";
        public string SuccessStatusLabel { get; set; } = "טופס נוצר בהצלחה";
        public string FileColumnId { get; set; } = "file_mm1bvngc";
        public string[] AllowedExtensions { get; set; } = ["docx", "doc"];
        public string TikNumberColumnId { get; set; } = "lookup_mm19bm4v";
        public int FileWaitTimeoutHours { get; set; } = 2;
        public int ItemsPageLimit { get; set; } = 50;
        /// <summary>When set, only process items matching this TikNumber (test filter). Null/empty = process all.</summary>
        public string? TestTikNumber { get; set; }
        /// <summary>HTTP timeout in seconds for Tasks board fetch. Default 120.</summary>
        public int FetchTimeoutSeconds { get; set; } = 120;
        /// <summary>When in test mode, fetch at most this many items per page to reduce payload. Default 10.</summary>
        public int TestModePageLimit { get; set; } = 10;
        public bool IsTestMode => !string.IsNullOrWhiteSpace(TestTikNumber);
    }

    public class AccidentStorySettings
    {
        public bool Enabled { get; set; } = false;
        /// <summary>When false, Accident Story is not written to Odcanit (emergency kill switch).</summary>
        public bool WriteEnabled { get; set; } = true;
        public string NispahType { get; set; } = "סיפור תאונה";
        public AccidentStoryColumnDef[] Columns { get; set; } = [];
    }

    public class AccidentStoryColumnDef
    {
        public string ColumnId { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public string Type { get; set; } = "text";
        public bool IncludeIfEmpty { get; set; } = false;
    }

    public class OdcanitDocumentSettings
    {
        public int CategoryCounter { get; set; } = 1;
        public int SubCategoryCounter { get; set; } = 0;
        public int DocStatus { get; set; } = 1;
        public int DocType { get; set; } = 8;
        public int WriterCounter { get; set; } = 1;
        public int OwnerCounter { get; set; } = 1;
        public int Metapel { get; set; } = 1;
    }
}
