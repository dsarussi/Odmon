namespace Odmon.Worker.Configuration
{
    public sealed class EmailAutomationSettings
    {
        public bool Enabled { get; set; }
        public bool DryRun { get; set; } = true;
        public bool RealForwardEnabled { get; set; }
        public int IntervalMinutes { get; set; } = 3;
        public int MaxMessagesPerCycle { get; set; } = 50;
        public int MaxForwardsPerCycle { get; set; } = 20;
        public string TenantId { get; set; } = string.Empty;
        public string ClientId { get; set; } = string.Empty;
        public string ClientSecret { get; set; } = string.Empty;
        public string FingerprintKey { get; set; } = string.Empty;
        public DateTime? StartProcessingFromUtc { get; set; }
        public List<EmailAutomationMailboxSettings> Mailboxes { get; set; } = [];
    }

    public sealed class EmailAutomationMailboxSettings
    {
        public string Address { get; set; } = "amir@ezer-law.com";
        public string InboxFolder { get; set; } = "Inbox";
        public List<EmailAutomationRuleSettings> Rules { get; set; } = [];
    }

    public sealed class EmailAutomationRuleSettings
    {
        public string Name { get; set; } = "CourtCaseRoutingTest";
        public bool Enabled { get; set; } = true;
        public string SubjectRegex { get; set; } = @"\b\d{1,6}-\d{2}-\d{2}\b";
        public bool TestForwardEnabled { get; set; }
        public string TestForwardTo { get; set; } = "odmon@ezer-law.com";
    }
}
