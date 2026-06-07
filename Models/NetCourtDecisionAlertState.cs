namespace Odmon.Worker.Models
{
    public class NetCourtDecisionAlertState
    {
        public int Id { get; set; }
        public long? LastSeenCounter { get; set; }

        /// <summary>
        /// Initialization timestamp retained for deployment diagnostics and compatibility.
        /// </summary>
        public DateTime? BaselineCompletedAtUtc { get; set; }
        public DateTime UpdatedAtUtc { get; set; }
    }
}
