namespace Odmon.Worker.Models
{
    public class NetCourtDecisionAlertState
    {
        public int Id { get; set; }
        /// <summary>
        /// Durable feature start point. Documents older than this timestamp are never considered.
        /// The legacy property name is retained for database compatibility.
        /// </summary>
        public DateTime? BaselineCompletedAtUtc { get; set; }
        public DateTime UpdatedAtUtc { get; set; }
    }
}
