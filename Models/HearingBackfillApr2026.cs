using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Odmon.Worker.Models
{
    /// <summary>
    /// Snapshot table for April 2026 hearings backfill. Source columns use Hebrew display names.
    /// Tracking columns (ImportStatus, ImportedAtUtc, MondayItemId, ImportError) added via migration.
    /// </summary>
    [Table("HearingBackfill_Apr2026")]
    public class HearingBackfillApr2026
    {
        [Key]
        public int Id { get; set; }

        [Column("תאריך דיון")]
        public DateTime? HearingDate { get; set; }

        [Column("שעת דיון")]
        public TimeSpan? HearingTime { get; set; }

        [Column("שם שופט")]
        [MaxLength(256)]
        public string? JudgeName { get; set; }

        [Column("שם ביהמש")]
        [MaxLength(256)]
        public string? CourtName { get; set; }

        [Column("טלפון נהג")]
        [MaxLength(64)]
        public string? DriverPhone { get; set; }

        [Column("שם נהג")]
        [MaxLength(256)]
        public string? DriverName { get; set; }

        [Column("מספר תיק")]
        [MaxLength(64)]
        public string? TikNumber { get; set; }

        [Column("מספר לקוח")]
        [MaxLength(32)]
        public string? ClientNumber { get; set; }

        [Column("תאריך אירוע")]
        public DateTime? EventDate { get; set; }

        // Tracking columns (added via migration)
        [Column("ImportStatus")]
        [MaxLength(20)]
        public string ImportStatus { get; set; } = "Pending";

        [Column("ImportedAtUtc")]
        public DateTime? ImportedAtUtc { get; set; }

        [Column("MondayItemId")]
        public long? MondayItemId { get; set; }

        [Column("ImportError")]
        [MaxLength(4000)]
        public string? ImportError { get; set; }

        /// <summary>UTC when import failed. Set when ImportStatus='Failed'.</summary>
        [Column("FailedAtUtc")]
        public DateTime? FailedAtUtc { get; set; }
    }
}
