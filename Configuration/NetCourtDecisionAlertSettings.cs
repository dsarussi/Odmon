namespace Odmon.Worker.Configuration
{
    public class NetCourtDecisionAlertSettings
    {
        public bool Enabled { get; set; } = false;
        public int IntervalSeconds { get; set; } = 300;
        public string StartFromDocDate { get; set; } = "2026-06-07";
        public int MaxBatchSize { get; set; } = 100;
        public bool AttachDecisionPdf { get; set; } = true;
        public long MaxAttachmentBytes { get; set; } = 26214400;
        public string[] AttachmentAllowedRoots { get; set; } =
        [
            @"\\dc22\Odlight\Docs\",
            @"D:\Odlight\Docs\"
        ];
        public string EmailMode { get; set; } = "Live";
        public string TestRecipient { get; set; } = "odmon@ezer-law.com";
        public string[] BccRecipients { get; set; } = ["odmon@ezer-law.com"];
        public Dictionary<int, string> ClientNumberToRecipientEmail { get; set; } = new();
        public bool FallbackRecipientEnabled { get; set; } = false;

        public bool IsTestMode =>
            !string.Equals(EmailMode, "Live", StringComparison.OrdinalIgnoreCase);
    }
}
