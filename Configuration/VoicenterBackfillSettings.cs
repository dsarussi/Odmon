namespace Odmon.Worker.Configuration
{
    /// <summary>
    /// One-time / manual backfill mode for Voicenter call summaries.
    /// Lets ops process a wider date range than the normal daily lookback.
    /// </summary>
    public class VoicenterBackfillSettings
    {
        /// <summary>Master switch. When true, the next worker cycle will run as a backfill instead of normal lookback.</summary>
        public bool Enable { get; set; } = false;

        /// <summary>Inclusive UTC start of the backfill window (e.g. 2026-04-27T00:00:00Z).</summary>
        public DateTime? FromUtc { get; set; }

        /// <summary>Exclusive/inclusive UTC end of the backfill window. If null, uses DateTime.UtcNow.</summary>
        public DateTime? ToUtc { get; set; }

        /// <summary>Hard cap on how many CDR rows to process in the run. 0 = no cap.</summary>
        public int MaxCalls { get; set; } = 0;

        /// <summary>If true, retries calls previously marked NoAI / NoMatch / Failed. Already-Written calls are still skipped.</summary>
        public bool ForceRecheck { get; set; } = false;

        /// <summary>Default true. When true: list CDRs and report what WOULD be done, but do not write to Odcanit and do not mark Written.</summary>
        public bool DryRun { get; set; } = true;
    }
}
