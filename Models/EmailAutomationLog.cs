namespace Odmon.Worker.Models
{
    public sealed class EmailAutomationLog
    {
        public long Id { get; set; }
        public string Mailbox { get; set; } = string.Empty;
        public string? RuleName { get; set; }
        public string? InternetMessageId { get; set; }
        public string GraphMessageId { get; set; } = string.Empty;
        public string? Subject { get; set; }
        public string? Sender { get; set; }
        public DateTime? ReceivedDateTimeUtc { get; set; }
        public string? DetectedCourtCaseNumber { get; set; }
        public string? TargetEmail { get; set; }
        public string Action { get; set; } = string.Empty;
        public string? IdempotencyKey { get; set; }
        public string? ErrorMessage { get; set; }
        public DateTime CreatedAtUtc { get; set; }
        public DateTime? ProcessedAtUtc { get; set; }
    }

    public static class EmailAutomationActions
    {
        public const string Read = "Read";
        public const string Matched = "Matched";
        public const string DryRunWouldForward = "DryRunWouldForward";
        public const string ForwardedToTestMailbox = "ForwardedToTestMailbox";
        public const string SkippedAlreadyProcessed = "SkippedAlreadyProcessed";
        public const string Failed = "Failed";
    }
}
