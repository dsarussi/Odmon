using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Odmon.Worker.Configuration;
using Odmon.Worker.Services;

namespace Odmon.Worker.Workers
{
    /// <summary>
    /// One-time backfill worker for hearing-approval annexes.
    /// Runs a single pass when HearingApprovalBackfill:Enable=true, then exits.
    /// </summary>
    public class HearingApprovalBackfillWorker : BackgroundService
    {
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly ILogger<HearingApprovalBackfillWorker> _logger;
        private readonly HearingApprovalBackfillSettings _settings;

        public HearingApprovalBackfillWorker(
            IServiceScopeFactory scopeFactory,
            ILogger<HearingApprovalBackfillWorker> logger,
            IOptions<HearingApprovalBackfillSettings> settings)
        {
            _scopeFactory = scopeFactory;
            _logger = logger;
            _settings = settings.Value;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            if (!_settings.Enable)
            {
                _logger.LogInformation("HearingApprovalBackfillWorker: Disabled (HearingApprovalBackfill:Enable=false). Exiting.");
                return;
            }

            _logger.LogInformation(
                "HearingApprovalBackfillWorker: Enabled. Starting one-time backfill (DryRun={DryRun}).",
                _settings.DryRun);

            try
            {
                using var scope = _scopeFactory.CreateScope();
                var service = scope.ServiceProvider.GetRequiredService<HearingApprovalBackfillService>();
                var result = await service.RunAsync(stoppingToken);

                _logger.LogInformation(
                    "HearingApprovalBackfillWorker: Completed. Scanned={Scanned}, Written={Written}, Failed={Failed}",
                    result.Scanned, result.Written, result.Failed);
            }
            catch (OperationCanceledException)
            {
                _logger.LogInformation("HearingApprovalBackfillWorker: Cancelled.");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "HearingApprovalBackfillWorker: Fatal error during backfill.");
            }
        }
    }
}
