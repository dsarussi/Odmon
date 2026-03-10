namespace Odmon.Worker.Configuration
{
    public class DocumentIngestionSettings
    {
        public bool Enabled { get; set; } = false;
        public long BoardId { get; set; } = 5088708083;
        public long LinkedCasesBoardId { get; set; } = 5035534500;
        public string InboxPath { get; set; } = @"D:\Odlight\OdmonInbox";
        public long MaxFileSizeBytes { get; set; } = 52428800;
        public string[] AllowedExtensions { get; set; } = ["pdf", "jpg", "jpeg", "png"];
        public string[] Columns { get; set; } = ["file_mm0qwtat", "file_mkzr2cmr"];
        public string RelationColumnId { get; set; } = "board_relation_mkzenscq";
        public string LinkedCaseTikColumnId { get; set; } = "text_mkwe19hn";
        public int IntervalSeconds { get; set; } = 300;
        public int MaxRetryCount { get; set; } = 3;
        public int CommandTimeoutSeconds { get; set; } = 60;
        public int ItemsPageLimit { get; set; } = 50;

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

        /// <summary>Optional Tasks board source (משימות). When enabled, also imports PDFs from items with status "טופס נוצר בהצלחה".</summary>
        public TasksBoardSourceSettings? TasksSource { get; set; }
    }

    public class TasksBoardSourceSettings
    {
        public bool Enabled { get; set; } = false;
        public long BoardId { get; set; } = 5035534505;
        public string TaskStatusColumnId { get; set; } = "color_mkwej7ys";
        public string SuccessStatusLabel { get; set; } = "טופס נוצר בהצלחה";
        public string FileColumnId { get; set; } = "file_mkwerwmq";
        public string TikNumberColumnId { get; set; } = "lookup_mm19bm4v";
        public int FileWaitTimeoutHours { get; set; } = 2;
        public int ItemsPageLimit { get; set; } = 50;
        /// <summary>When set, only process items matching this TikNumber (test filter). Null/empty = process all.</summary>
        public string? TestTikNumber { get; set; }
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
