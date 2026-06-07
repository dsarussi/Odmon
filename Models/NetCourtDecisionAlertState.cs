namespace Odmon.Worker.Models
{
    public class NetCourtDecisionAlertState
    {
        public int Id { get; set; }
        public DateTime? BaselineCompletedAtUtc { get; set; }
        public DateTime UpdatedAtUtc { get; set; }
    }
}
