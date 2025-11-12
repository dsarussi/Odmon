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

        public SyncWorker(IServiceScopeFactory scopeFactory, ILogger<SyncWorker> logger, IConfiguration config)
        {
            _scopeFactory = scopeFactory;
            _logger = logger;
            _config = config;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            var interval = _config.GetValue<int>("Sync:IntervalSeconds", 30);
            var timer = new PeriodicTimer(TimeSpan.FromSeconds(interval));

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    using var scope = _scopeFactory.CreateScope();
                    var syncService = scope.ServiceProvider.GetRequiredService<SyncService>();

                    await syncService.SyncOdcanitToMondayAsync(stoppingToken);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error during Odcanit→Monday sync");
                }

                await timer.WaitForNextTickAsync(stoppingToken);
            }
        }
    }
}
