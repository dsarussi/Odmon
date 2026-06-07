namespace Odmon.Worker.Models
{
    /// <summary>
    /// Read-only row from Odcanit vwNetCourtDocs.
    /// Decision eligibility is determined exclusively by DocType 2 or 3.
    /// </summary>
    public class NetCourtDocument
    {
        public long Counter { get; set; }
        public int TikCounter { get; set; }
        public long? ODDocID { get; set; }
        public long? CourtDocumentID { get; set; }
        public int DocType { get; set; }
        public DateTime? DocDate { get; set; }
        public DateTime? tsCreateDate { get; set; }
        public string? Description { get; set; }
        public string? DecisionDesc { get; set; }
        public long? DecisionID { get; set; }
    }
}
