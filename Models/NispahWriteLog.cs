namespace Odmon.Worker.Models
{
    /// <summary>
    /// Durable idempotency log for Nispah writes to Odcanit.
    /// Insert-first: we insert here before calling Odcanit; unique constraint prevents duplicate writes across runs and concurrent processes.
    /// </summary>
    public class NispahWriteLog
    {
        public long Id { get; set; }
        public int TikCounter { get; set; }
        public string? TikVisualId { get; set; }
        public string NispahType { get; set; } = string.Empty;
        /// <summary>e.g. AccidentStory, PdfAsset</summary>
        public string SourceKind { get; set; } = string.Empty;
        public long SourceItemId { get; set; }
        public long? SourceAssetId { get; set; }
        public string InfoHash { get; set; } = string.Empty;
        public DateTime CreatedAtUtc { get; set; }
        public bool Failed { get; set; }
        public string? ErrorMessage { get; set; }
    }
}
