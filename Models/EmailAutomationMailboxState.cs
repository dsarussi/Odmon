namespace Odmon.Worker.Models
{
    public sealed class EmailAutomationMailboxState
    {
        public long Id { get; set; }
        public string Mailbox { get; set; } = string.Empty;
        public string FolderId { get; set; } = string.Empty;
        public string? DeltaLink { get; set; }
        public DateTime ProcessingFromUtc { get; set; }
        public DateTime? LastSuccessfulSyncUtc { get; set; }
        public DateTime CreatedAtUtc { get; set; }
        public DateTime UpdatedAtUtc { get; set; }
    }
}
