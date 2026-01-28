namespace Odmon.Worker.Models
{
    public class NispahAuditLog
    {
        public long Id { get; set; }
        public DateTime CreatedAtUtc { get; set; }
        public string CorrelationId { get; set; } = string.Empty;
        public string TikVisualID { get; set; } = string.Empty;
        public string NispahTypeName { get; set; } = string.Empty;
        public int InfoLength { get; set; }
        public string InfoHash { get; set; } = string.Empty; // SHA-256 hash
        public string Status { get; set; } = string.Empty; // Success, Failed, Blocked, DuplicateSkipped
        public string? Error { get; set; }
    }
}
