using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Threading;
using System.Threading.Tasks;
using Odmon.Worker.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Odmon.Worker.Workers
{
    public class SyncWorker : BackgroundService
    {
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly ILogger<SyncWorker> _logger;
        private readonly IConfiguration _config;
        private readonly IErrorNotifier _errorNotifier;

        public SyncWorker(
            IServiceScopeFactory scopeFactory,
            ILogger<SyncWorker> logger,
            IConfiguration config,
            IErrorNotifier errorNotifier)
        {
            _scopeFactory = scopeFactory;
            _logger = logger;
            _config = config;
            _errorNotifier = errorNotifier;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            var interval = _config.GetValue<int>("Sync:IntervalSeconds", 30);
            var timer = new PeriodicTimer(TimeSpan.FromSeconds(interval));

            while (!stoppingToken.IsCancellationRequested)
            {
                var runId = Guid.NewGuid().ToString("N")[..12];
                try
                {
                    using var scope = _scopeFactory.CreateScope();
                    var syncService = scope.ServiceProvider.GetRequiredService<SyncService>();

                    await syncService.SyncOdcanitToMondayAsync(stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    _logger.LogInformation("Worker shutdown requested.");
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogCritical(ex, "WORKER CRASH during Odcanit→Monday sync. RunId={RunId}", runId);

                    try
                    {
                        await _errorNotifier.NotifyWorkerCrashAsync(runId, ex, stoppingToken);
                    }
                    catch (Exception nex)
                    {
                        _logger.LogWarning(nex, "Error notifier failed during worker crash handling");
                    }
                }

                await timer.WaitForNextTickAsync(stoppingToken);
            }
        }
    }
}
