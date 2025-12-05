using System.ComponentModel.DataAnnotations.Schema;

namespace Odmon.Worker.Models
{
    public class OdcanitHozlapMainData
    {
        [Column("Counter")]
        public int TikCounter { get; set; }
        public string? clcCourtNum { get; set; }
        public string? CourtName { get; set; }
    }
}

