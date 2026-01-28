namespace Odmon.Worker.Models
{
    public class NispahDeduplication
    {
        public long Id { get; set; }
        public DateTime CreatedAtUtc { get; set; }
        public string TikVisualID { get; set; } = string.Empty;
        public string NispahTypeName { get; set; } = string.Empty;
        public string InfoHash { get; set; } = string.Empty; // SHA-256 hash
    }
}
