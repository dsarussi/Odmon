namespace Odmon.Worker.Configuration
{
    public class HearingBackfillSettings
    {
        public bool Enable { get; set; }
        public string SourceTable { get; set; } = "dbo.HearingBackfill_Apr2026";
        public long BoardId { get; set; } = 5035534500;
        public string StatusColumnId { get; set; } = "color_mm12y7zr";
        public int ImportedStatusIndex { get; set; } = 1;
        public int BatchSize { get; set; } = 50;
    }
}
