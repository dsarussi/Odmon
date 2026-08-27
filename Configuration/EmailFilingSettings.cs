namespace Odmon.Worker.Configuration
{
    /// <summary>
    /// Isolated safety controls for filing inbound email content into Odcanit.
    /// Real-write controls remain isolated while the final Odcanit email-file
    /// contract is being verified. No production writer is wired in V1 yet.
    /// </summary>
    public sealed class EmailFilingSettings
    {
        public bool Enabled { get; set; }
        public bool DryRun { get; set; } = true;
        public bool RealWriteEnabled { get; set; }
        public int IntervalMinutes { get; set; } = 3;
        public int MaxMessagesPerCycle { get; set; } = 50;
        public DateTime? StartProcessingFromUtc { get; set; }
        public List<EmailFilingAllowlistEntry> RealWriteAllowlist { get; set; } =
        [
            new()
            {
                TikNumber = "9/1984",
                TikCounter = 40514
            }
        ];

        public bool IsRealWriteAllowlisted(string tikNumber, int tikCounter)
            => RealWriteAllowlist?.Any(entry =>
                entry.TikCounter == tikCounter &&
                string.Equals(
                    entry.TikNumber?.Trim(),
                    tikNumber.Trim(),
                    StringComparison.Ordinal)) == true;
    }

    public sealed class EmailFilingAllowlistEntry
    {
        public string TikNumber { get; set; } = string.Empty;
        public int TikCounter { get; set; }
    }
}
