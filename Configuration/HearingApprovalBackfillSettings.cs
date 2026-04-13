namespace Odmon.Worker.Configuration
{
    public class HearingApprovalBackfillSettings
    {
        public bool Enable { get; set; }

        /// <summary>Log-only mode: detects changes and logs annex text but does not write or advance state.</summary>
        public bool DryRun { get; set; } = true;

        /// <summary>Cap the number of mappings processed (0 = unlimited).</summary>
        public int MaxItems { get; set; }

        /// <summary>When set, only process these specific TikCounters (useful for targeted testing).</summary>
        public int[]? OnlyTikCounters { get; set; }

        /// <summary>Milliseconds to wait between Monday API calls to avoid rate limits (default 200).</summary>
        public int ThrottleMs { get; set; } = 200;
    }
}
