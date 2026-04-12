using System.ComponentModel.DataAnnotations.Schema;

namespace Odmon.Worker.Models
{
    /// <summary>
    /// Row shape for hearings backfill staging tables (e.g. HearingBackfill_Apr2026, HearingBackfill_May2026).
    /// Queries use FromSqlRaw against HearingBackfill:SourceTable.
    /// </summary>
    [Table("HearingBackfill_Apr2026")]
    public class HearingBackfillApr2026
    {
        [Column("תאריך דיון")]
        public DateTime? HearingDate { get; set; }

        /// <summary>May be time or nvarchar in source table; read as string, parsed in BuildColumnValues.</summary>
        [Column("שעת דיון")]
        public string? HearingTime { get; set; }

        [Column("שם שופט")]
        public string? JudgeName { get; set; }

        [Column("עיר בית משפט")]
        public string? CourtCity { get; set; }

        [Column("טלפון נהג")]
        public string? DriverPhone { get; set; }

        [Column("שם נהג")]
        public string? DriverName { get; set; }

        [Column("מספר תיק")]
        public string? TikNumber { get; set; }

        /// <summary>May be int or nvarchar in source table; read as string for materialization.</summary>
        [Column("מספר לקוח")]
        public string? ClientNumber { get; set; }

        [Column("תאריך אירוע")]
        public DateTime? EventDate { get; set; }
    }
}
