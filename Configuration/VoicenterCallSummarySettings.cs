namespace Odmon.Worker.Configuration
{
    public class VoicenterCallSummarySettings
    {
        public bool Enabled { get; set; }
        public double IntervalHours { get; set; } = 12;
        public double LookbackHours { get; set; } = 25;

        /// <summary>Voicenter CDR code — resolved via ISecretProvider at runtime.</summary>
        public string? Code { get; set; }
        /// <summary>Voicenter Bearer token — resolved via ISecretProvider at runtime.</summary>
        public string? BearerToken { get; set; }

        public string NispahTypeName { get; set; } = "סיכום שיחה";
        public bool OnlyAnsweredCalls { get; set; } = true;
        public int MinimumDurationSeconds { get; set; } = 10;
        public bool EnableDailySummaryReporting { get; set; } = true;

        /// <summary>When TestMode=true and TestCallId is set, only process that single call.</summary>
        public string? TestCallId { get; set; }
        public bool TestMode { get; set; }

        /// <summary>Throttle delay in ms between call-detail API requests.</summary>
        public int ThrottleMs { get; set; } = 500;

        // ─── Alerting & resilience ───

        public bool AlertOnUnhandledException { get; set; } = true;
        public bool AlertOnStaleWorker { get; set; } = true;
        public double StaleWorkerThresholdHours { get; set; } = 18;
        public int FailureAlertCooldownMinutes { get; set; } = 60;

        // ─── Voicenter weekly quota guard rails (apply to CallHistoryDetail endpoint) ───

        /// <summary>
        /// When CallHistoryDetail weekly request count reaches this threshold, send a warning email
        /// (once per week). Does NOT block requests; only alerts.
        /// </summary>
        public int WeeklyUsageWarningThreshold { get; set; } = 350;

        /// <summary>
        /// Voicenter-enforced hard weekly limit for CallHistoryDetail. Used in alert messaging only;
        /// the actual block comes from Voicenter's HTTP 401 response which we detect and surface.
        /// </summary>
        public int WeeklyUsageHardLimit { get; set; } = 400;

        /// <summary>If false, no warning email is sent even when threshold is reached.</summary>
        public bool UsageWarningEmailEnabled { get; set; } = true;

        /// <summary>Retry calls that were skipped after CallHistoryDetail quota was exhausted. 0 disables.</summary>
        public int ReprocessQuotaExceededLookbackDays { get; set; } = 14;

        /// <summary>Retry recent NoMatch calls after resolver/phone-data changes. 0 disables.</summary>
        public int ReprocessNoMatchLookbackDays { get; set; } = 0;

        /// <summary>Hard cap for deferred state retries per cycle.</summary>
        public int MaxDeferredReprocessCallsPerRun { get; set; } = 50;

        /// <summary>
        /// Odcanit UserData field names that may contain phone numbers for Voicenter matching.
        /// When empty, the resolver uses its built-in Odcanit phone-field allowlist.
        /// </summary>
        public string[] PhoneFieldNames { get; set; } = [];
    }
}
