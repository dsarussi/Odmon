namespace Odmon.Worker.Configuration
{
    public class NetCourtDecisionAlertSettings
    {
        public bool Enabled { get; set; } = false;
        public int IntervalSeconds { get; set; } = 300;
        public int LookbackDays { get; set; } = 7;
        public bool BaselineOnlyOnFirstRun { get; set; } = true;
        public string EmailMode { get; set; } = "Test";
        public string TestRecipient { get; set; } = "odmon@ezer-law.com";
        public Dictionary<int, string> ClientNumberToRecipientEmail { get; set; } = new();
        public bool FallbackRecipientEnabled { get; set; } = false;

        public bool IsTestMode =>
            !string.Equals(EmailMode, "Live", StringComparison.OrdinalIgnoreCase);
    }
}
