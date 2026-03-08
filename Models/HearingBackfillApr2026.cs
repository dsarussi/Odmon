using System.ComponentModel.DataAnnotations.Schema;

namespace Odmon.Worker.Models
{
    /// <summary>
    /// Snapshot table for April 2026 hearings backfill. Read-only; no tracking columns.
    /// Source: [OdmonIntegration].[dbo].[HearingBackfill_Apr2026]
    /// </summary>
    [Table("HearingBackfill_Apr2026")]
    public class HearingBackfillApr2026
    {
        [Column("תאריך דיון")]
        public DateTime? HearingDate { get; set; }

        [Column("שעת דיון")]
        public TimeSpan? HearingTime { get; set; }

        [Column("שם שופט")]
        public string? JudgeName { get; set; }

        [Column("שם ביהמש")]
        public string? CourtName { get; set; }

        [Column("טלפון נהג")]
        public string? DriverPhone { get; set; }

        [Column("שם נהג")]
        public string? DriverName { get; set; }

        [Column("מספר תיק")]
        public string? TikNumber { get; set; }

        [Column("מספר לקוח")]
        public string? ClientNumber { get; set; }

        [Column("תאריך אירוע")]
        public DateTime? EventDate { get; set; }
    }
}
