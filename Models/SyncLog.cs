namespace Odmon.Worker.Models
{
    public class SyncLog
    {
        public int Id { get; set; }
        public DateTime CreatedAtUtc { get; set; }
        public string Source { get; set; } = string.Empty;
        public string Level { get; set; } = "Info";
        public string Message { get; set; } = string.Empty;
        public string? Details { get; set; }
    }
}

