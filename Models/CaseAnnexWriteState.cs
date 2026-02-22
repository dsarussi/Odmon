namespace Odmon.Worker.Models
{
    /// <summary>
    /// Per-case persistent flags for annex writes. Used to enforce idempotency (e.g. Accident Story note written at most once per TikCounter).
    /// </summary>
    public class CaseAnnexWriteState
    {
        public int TikCounter { get; set; }

        public bool AccidentStoryAnnexWritten { get; set; }
        public DateTime? AccidentStoryAnnexWrittenAtUtc { get; set; }
        public string? AccidentStoryAnnexWrittenRunId { get; set; }
    }
}
