using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Odmon.Worker.Data;
using Odmon.Worker.Services;

namespace Odmon.Worker.Workers
{
    public class DocumentIngestionWorker : BackgroundService
    {
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly ILogger<DocumentIngestionWorker> _logger;
        private readonly IConfiguration _config;
        private readonly IEmailNotifier _emailNotifier;

        private DateTime _workerStartedAtUtc;
        private int _totalRunsCompleted;
        private int _totalFailures;

        public DocumentIngestionWorker(
            IServiceScopeFactory scopeFactory,
            ILogger<DocumentIngestionWorker> logger,
            IConfiguration config,
            IEmailNotifier emailNotifier)
        {
            _scopeFactory = scopeFactory;
            _logger = logger;
            _config = config;
            _emailNotifier = emailNotifier;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            var enabled = _config.GetValue<bool>("MondayDocumentIngestion:Enabled", false);
            if (!enabled)
            {
                _logger.LogInformation("DocumentIngestionWorker is DISABLED (MondayDocumentIngestion:Enabled=false). Idling.");
                try { await Task.Delay(Timeout.Infinite, stoppingToken); }
                catch (OperationCanceledException) { }
                return;
            }

            _workerStartedAtUtc = DateTime.UtcNow;
            var intervalSeconds = _config.GetValue<int>("MondayDocumentIngestion:IntervalSeconds", 300);
            var boardId = _config.GetValue<long>("MondayDocumentIngestion:BoardId", 0);

            _logger.LogInformation(
                "DocumentIngestionWorker STARTED | Board={BoardId}, Interval={IntervalSeconds}s",
                boardId, intervalSeconds);

            await CheckNispahDedupTableAsync(stoppingToken);

            var timer = new PeriodicTimer(TimeSpan.FromSeconds(intervalSeconds));
            bool firstRun = true;

            while (!stoppingToken.IsCancellationRequested)
            {
                if (!firstRun)
                {
                    try
                    {
                        await timer.WaitForNextTickAsync(stoppingToken);
                    }
                    catch (OperationCanceledException) { break; }
                }
                firstRun = false;

                if (stoppingToken.IsCancellationRequested) break;

                var runId = Guid.NewGuid().ToString("N")[..12];
                try
                {
                    _logger.LogInformation("DocumentIngestionWorker run {RunId} starting", runId);

                    using var scope = _scopeFactory.CreateScope();
                    var service = scope.ServiceProvider.GetRequiredService<DocumentIngestionService>();
                    await service.RunIngestionAsync(stoppingToken, runId);

                    _totalRunsCompleted++;
                    _logger.LogInformation("DocumentIngestionWorker run {RunId} completed", runId);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    _logger.LogInformation("DocumentIngestionWorker shutting down gracefully.");
                    break;
                }
                catch (Exception ex)
                {
                    _totalFailures++;
                    _logger.LogCritical(ex,
                        "DocumentIngestionWorker CRASH during run {RunId}", runId);

                    _emailNotifier.QueueCriticalAlert(
                        $"DocumentIngestionWorker crash (run {runId})",
                        $"Exception: {ex.Message}\nType: {ex.GetType().Name}\nStack: {ex.StackTrace?[..Math.Min(ex.StackTrace?.Length ?? 0, 500)]}",
                        ex.GetType().Name,
                        "DocumentIngestionWorker");
                }
            }

            _logger.LogInformation(
                "DocumentIngestionWorker STOPPED | Uptime={UptimeMin:F1} min, Runs={Runs}, Failures={Failures}",
                (DateTime.UtcNow - _workerStartedAtUtc).TotalMinutes,
                _totalRunsCompleted, _totalFailures);
        }

        private async Task CheckNispahDedupTableAsync(CancellationToken ct)
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<IntegrationDbContext>();
                await db.NispahDeduplications.AsNoTracking().Take(0).CountAsync(ct);
                _logger.LogInformation("HEALTHCHECK | NispahDeduplications table exists in IntegrationDb — dedup is active.");
            }
            catch (SqlException sqlEx) when (sqlEx.Number == 208)
            {
                _logger.LogWarning(
                    "HEALTHCHECK | NispahDeduplications table NOT FOUND in IntegrationDb (SqlException 208). Dedup will be bypassed at runtime. Run EF migrations to create the table.");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "HEALTHCHECK | Could not verify NispahDeduplications table. Dedup status unknown.");
            }
        }
    }
}
