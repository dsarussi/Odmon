using System.ComponentModel.DataAnnotations.Schema;

namespace Odmon.Worker.Models
{
    public class OdcanitCase
    {
        public int SideCounter { get; set; }
        public int TikCounter { get; set; }
        public string TikNumber { get; set; } = string.Empty;
        public string TikName { get; set; } = string.Empty;
        public string ClientName { get; set; } = string.Empty;
        public string StatusName { get; set; } = string.Empty;
        public int? TikOwner { get; set; }
        public DateTime tsCreateDate { get; set; }
        public DateTime tsModifyDate { get; set; }
        public string? Notes { get; set; }
        public string? ClientVisualID { get; set; }
        public string? HozlapTikNumber { get; set; }
        [NotMapped]
        public string? ClientPhone { get; set; }
        [NotMapped]
        public string? ClientEmail { get; set; }
        [NotMapped]
        public DateTime? EventDate { get; set; }
        [NotMapped]
        public decimal? ClaimAmount { get; set; }
    }
}

