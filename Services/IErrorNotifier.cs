namespace Odmon.Worker.Services
{
    /// <summary>
    /// Pluggable error notification interface.
    /// Implementations can send emails, Slack messages, webhooks, etc.
    /// Failures in notification itself must never crash the worker.
    /// </summary>
    public interface IErrorNotifier
    {
        /// <summary>
        /// Called when the worker catches an unhandled exception at the top level.
        /// </summary>
        Task NotifyWorkerCrashAsync(string runId, Exception exception, CancellationToken ct);

        /// <summary>
        /// Called when a sync run has a high failure rate (e.g., > 50% of cases failed).
        /// </summary>
        Task NotifyHighFailureRateAsync(string runId, int totalCases, int failedCases, CancellationToken ct);
    }
}
