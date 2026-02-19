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
