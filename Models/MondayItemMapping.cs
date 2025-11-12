namespace Odmon.Worker.Models
{
    public class MondayItemMapping
    {
        public int Id { get; set; }
        public int TikCounter { get; set; }
        public long MondayItemId { get; set; }
        public DateTime? LastSyncFromOdcanitUtc { get; set; }
        public DateTime? LastSyncFromMondayUtc { get; set; }
        public string? OdcanitVersion { get; set; }
        public string? MondayChecksum { get; set; }
    }
}

