using System.ComponentModel.DataAnnotations.Schema;

namespace Odmon.Worker.Models
{
    public class MondayDocumentImport
    {
        public int Id { get; set; }
        public DateTime CreatedAtUtc { get; set; }
        public DateTime UpdatedAtUtc { get; set; }

        public int? TikCounter { get; set; }
        public string? TikVisualID { get; set; }

        public long MondayQuestionnaireItemId { get; set; }
        public long? LinkedCaseItemId { get; set; }

        public string ColumnId { get; set; } = string.Empty;
        public string AssetId { get; set; } = string.Empty;
        public string OriginalFileName { get; set; } = string.Empty;
        public long FileSizeBytes { get; set; }

        public string? InboxFilePath { get; set; }
        public int? OdcanitDocCounter { get; set; }
        public string? OdcanitDestPath { get; set; }

        public DocumentImportStatus Status { get; set; } = DocumentImportStatus.Pending;
        public string? ErrorMessage { get; set; }
        public int RetryCount { get; set; }
        public DateTime? LastAttemptAtUtc { get; set; }
        public bool AlertSent { get; set; }

        /// <summary>In-memory only (not persisted). Set during download for proof-of-write logging.</summary>
        [NotMapped]
        public string? LastDetectionSource { get; set; }

        /// <summary>In-memory only (not persisted). Set when extension was detected from Content-Type.</summary>
        [NotMapped]
        public string? LastDetectedMimeType { get; set; }
    }

    public enum DocumentImportStatus
    {
        Pending = 0,
        Downloaded = 1,
        SpCreated = 2,
        Copied = 3,
        Verified = 4,
        Success = 5,
        Failed = 6
    }
}
