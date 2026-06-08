using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Odmon.Worker.Services;

namespace Odmon.Worker.Workers
{
    public class NetCourtDecisionAlertWorker : BackgroundService
    {
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly IConfiguration _configuration;
        private readonly IEmailNotifier _emailNotifier;
        private readonly ILogger<NetCourtDecisionAlertWorker> _logger;

        public NetCourtDecisionAlertWorker(
            IServiceScopeFactory scopeFactory,
            IConfiguration configuration,
            IEmailNotifier emailNotifier,
            ILogger<NetCourtDecisionAlertWorker> logger)
        {
            _scopeFactory = scopeFactory;
            _configuration = configuration;
            _emailNotifier = emailNotifier;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            if (!_configuration.GetValue<bool>("NetCourtDecisionAlerts:Enabled", false))
            {
                _logger.LogInformation(
                    "NetCourtDecisionAlertWorker is disabled (NetCourtDecisionAlerts:Enabled=false).");
                try { await Task.Delay(Timeout.Infinite, stoppingToken); }
                catch (OperationCanceledException) { }
                return;
            }

            var intervalSeconds = Math.Max(
                30,
                _configuration.GetValue<int>("NetCourtDecisionAlerts:IntervalSeconds", 300));
            _logger.LogInformation(
                "NetCourtDecisionAlertWorker started. IntervalSeconds={IntervalSeconds}, EmailMode={EmailMode}",
                intervalSeconds,
                _configuration["NetCourtDecisionAlerts:EmailMode"] ?? "Test");

            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(intervalSeconds));
            var firstRun = true;

            while (!stoppingToken.IsCancellationRequested)
            {
                if (!firstRun)
                {
                    try
                    {
                        await timer.WaitForNextTickAsync(stoppingToken);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                }
                firstRun = false;

                try
                {
                    using var scope = _scopeFactory.CreateScope();
                    var service = scope.ServiceProvider.GetRequiredService<NetCourtDecisionAlertService>();
                    await service.RunAsync(stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "NetCourtDecisionAlertWorker run failed.");
                    QueueOperationalFailure(_emailNotifier, ex);
                }
            }

            _logger.LogInformation("NetCourtDecisionAlertWorker stopped.");
        }

        internal static void QueueOperationalFailure(IEmailNotifier emailNotifier, Exception exception)
        {
            emailNotifier.QueueCriticalAlert(
                "NetCourt decision alert worker failed",
                exception.ToString(),
                exception.GetType().FullName,
                nameof(NetCourtDecisionAlertWorker),
                "NetCourt Decision Alert Failure");
        }
    }
}
