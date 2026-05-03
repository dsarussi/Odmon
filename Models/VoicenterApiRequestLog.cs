namespace Odmon.Worker.Models
{
    /// <summary>
    /// Audit row for every outbound Voicenter API request.
    /// Used for weekly quota tracking, separated by endpoint type.
    /// </summary>
    public class VoicenterApiRequestLog
    {
        public long Id { get; set; }
        public DateTime CreatedAtUtc { get; set; }
        /// <summary>Start (UTC, Monday 00:00) of the ISO week the request belongs to.</summary>
        public DateTime WeekStartUtc { get; set; }
        public string EndpointType { get; set; } = "Other";
        public string? CallId { get; set; }
        public int? HttpStatus { get; set; }
        public bool Success { get; set; }
        public bool QuotaExceeded { get; set; }
        public string? ErrorMessage { get; set; }
        public string? CorrelationId { get; set; }
    }
}
