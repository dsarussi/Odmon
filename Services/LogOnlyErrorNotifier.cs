using Microsoft.Extensions.Logging;

namespace Odmon.Worker.Services
{
    /// <summary>
    /// Stub implementation that logs notifications.
    /// Replace with an email/webhook implementation when SMTP credentials are available.
    /// </summary>
    public class LogOnlyErrorNotifier : IErrorNotifier
    {
        private readonly ILogger<LogOnlyErrorNotifier> _logger;

        public LogOnlyErrorNotifier(ILogger<LogOnlyErrorNotifier> logger)
        {
            _logger = logger;
        }

        public Task NotifyWorkerCrashAsync(string runId, Exception exception, CancellationToken ct)
        {
            _logger.LogCritical(exception,
                "NOTIFICATION | Worker crash detected. RunId={RunId}, Error={Error}. " +
                "ACTION REQUIRED: Check logs and restart the service if needed.",
                runId, exception.Message);
            return Task.CompletedTask;
        }

        public Task NotifyHighFailureRateAsync(string runId, int totalCases, int failedCases, CancellationToken ct)
        {
            var failurePercent = totalCases > 0 ? (failedCases * 100.0 / totalCases) : 0;
            _logger.LogCritical(
                "NOTIFICATION | High failure rate in run {RunId}: {FailedCases}/{TotalCases} cases failed ({FailurePercent:F0}%). " +
                "ACTION REQUIRED: Review SyncFailures table and error logs.",
                runId, failedCases, totalCases, failurePercent);
            return Task.CompletedTask;
        }
    }
}
