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
    /// Runs the hearings backfill when HearingBackfill:Enable=true (source table from HearingBackfill:SourceTable).
    /// Processes batches until no pending rows remain.
    /// </summary>
    public class HearingBackfillWorker : BackgroundService
    {
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly ILogger<HearingBackfillWorker> _logger;
        private readonly HearingBackfillSettings _settings;

        public HearingBackfillWorker(
            IServiceScopeFactory scopeFactory,
            ILogger<HearingBackfillWorker> logger,
            IOptions<HearingBackfillSettings> settings)
        {
            _scopeFactory = scopeFactory;
            _logger = logger;
            _settings = settings.Value;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            if (!_settings.Enable)
            {
                _logger.LogInformation("HearingBackfillWorker: Disabled (HearingBackfill:Enable=false). Exiting.");
                return;
            }

            _logger.LogInformation(
                "HearingBackfillWorker: Enabled. Starting backfill loop (SourceTable={SourceTable}, BoardId={BoardId}, BatchSize={BatchSize}).",
                _settings.SourceTable, _settings.BoardId, _settings.BatchSize);

            var intervalSeconds = 300; // 5 minutes between batches
            var timer = new PeriodicTimer(TimeSpan.FromSeconds(intervalSeconds));

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    using var scope = _scopeFactory.CreateScope();
                    var service = scope.ServiceProvider.GetRequiredService<HearingBackfillService>();
                    var result = await service.RunAsync(stoppingToken);

                    if (result.NoMoreRows)
                    {
                        _logger.LogInformation("HearingBackfillWorker: No more rows to process. Stopping backfill loop.");
                        return;
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "HearingBackfillWorker: Error during backfill run.");
                }

                try
                {
                    await timer.WaitForNextTickAsync(stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
    }
}
