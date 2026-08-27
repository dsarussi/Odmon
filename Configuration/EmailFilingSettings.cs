namespace Odmon.Worker.Configuration
{
    /// <summary>
    /// Isolated safety controls for filing inbound email content into Odcanit.
    /// Real-write controls remain isolated around the verified Odcanit document
    /// row and protected-path copy contract.
    /// </summary>
    public sealed class EmailFilingSettings
    {
        public bool Enabled { get; set; }
        public bool DryRun { get; set; } = true;
        public bool RealWriteEnabled { get; set; }
        public int IntervalMinutes { get; set; } = 3;
        public int MaxMessagesPerCycle { get; set; } = 50;
        public long MaxMimeMessageBytes { get; set; } = 52428800;
        public long MaxDeltaPageBytes { get; set; } = 52428800;
        public int MaxMimeAttachmentCount { get; set; } = 100;
        public int MaxIdentifierCandidates { get; set; } = 100;
        public DateTime? StartProcessingFromUtc { get; set; }
        public bool AllowHistoricalBackfill { get; set; }
        public List<string> AllowedDestinationRoots { get; set; } = [];
        public List<EmailFilingAllowlistEntry> RealWriteAllowlist { get; set; } = [];

        public bool IsRealWriteAllowlisted(string tikNumber, int tikCounter)
            => IsRealWriteAllowlistConfigurationValid() &&
               RealWriteAllowlist.Any(entry =>
                entry.TikCounter == tikCounter &&
                string.Equals(
                    entry.TikNumber?.Trim(),
                    tikNumber.Trim(),
                    StringComparison.Ordinal)) == true;

        public bool IsRealWriteAllowlistConfigurationValid()
        {
            if (RealWriteAllowlist == null || RealWriteAllowlist.Count == 0)
                return false;

            var normalized = new List<(string TikNumber, int TikCounter)>();
            foreach (var entry in RealWriteAllowlist)
            {
                var tikNumber = entry?.TikNumber?.Trim();
                if (entry == null ||
                    entry.TikCounter <= 0 ||
                    string.IsNullOrWhiteSpace(tikNumber) ||
                    !System.Text.RegularExpressions.Regex.IsMatch(
                        tikNumber,
                        @"^[0-9]+/[0-9]+$",
                        System.Text.RegularExpressions.RegexOptions.CultureInvariant,
                        TimeSpan.FromSeconds(1)))
                {
                    return false;
                }

                normalized.Add((tikNumber, entry.TikCounter));
            }

            return normalized
                       .GroupBy(value => value.TikNumber, StringComparer.Ordinal)
                       .All(group => group.Select(value => value.TikCounter).Distinct().Count() == 1) &&
                   normalized
                       .GroupBy(value => value.TikCounter)
                       .All(group => group.Select(value => value.TikNumber).Distinct(StringComparer.Ordinal).Count() == 1);
        }

        public bool IsDestinationRootConfigurationValid()
            => AllowedDestinationRoots is { Count: > 0 } &&
               AllowedDestinationRoots.All(root =>
                   !string.IsNullOrWhiteSpace(root) && Path.IsPathFullyQualified(root));
    }

    public sealed class EmailFilingAllowlistEntry
    {
        public string TikNumber { get; set; } = string.Empty;
        public int TikCounter { get; set; }
    }
}
