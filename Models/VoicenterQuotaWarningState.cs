namespace Odmon.Worker.Models
{
    /// <summary>
    /// Records that a weekly quota warning was already emailed for a given (week, endpoint type),
    /// to prevent warning storms within the same week.
    /// </summary>
    public class VoicenterQuotaWarningState
    {
        public long Id { get; set; }
        public DateTime WeekStartUtc { get; set; }
        public string EndpointType { get; set; } = "CallHistoryDetail";
        public DateTime WarningSentAtUtc { get; set; }
        public int RequestCountAtWarning { get; set; }
        public int Threshold { get; set; }
        public int HardLimit { get; set; }
    }
}
