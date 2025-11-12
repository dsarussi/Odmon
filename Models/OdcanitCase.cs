namespace Odmon.Worker.Models
{
    public class OdcanitCase
    {
        public int TikCounter { get; set; }
        public string TikNumber { get; set; } = string.Empty;
        public string TikName { get; set; } = string.Empty;
        public string ClientName { get; set; } = string.Empty;
        public string StatusName { get; set; } = string.Empty;
        public int? TikOwner { get; set; }
        public DateTime tsCreateDate { get; set; }
        public DateTime tsModifyDate { get; set; }
        public string? Notes { get; set; }
    }
}

