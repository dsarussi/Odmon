using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Diagnostics;
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
        private readonly IHostEnvironment _hostEnv;

        // Heartbeat state
        private DateTime _workerStartedAtUtc;
        private DateTime _lastSyncCompletedAtUtc;
        private int _totalRunsCompleted;
        private int _totalFailures;

        public SyncWorker(
            IServiceScopeFactory scopeFactory,
            ILogger<SyncWorker> logger,
            IConfiguration config,
            IErrorNotifier errorNotifier,
            IHostEnvironment hostEnv)
        {
            _scopeFactory = scopeFactory;
            _logger = logger;
            _config = config;
            _errorNotifier = errorNotifier;
            _hostEnv = hostEnv;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _workerStartedAtUtc = DateTime.UtcNow;

            // ── Startup diagnostics ──
            LogStartupDiagnostics();

            var intervalSeconds = _config.GetValue<int>("Sync:IntervalSeconds", 1200);
            var timer = new PeriodicTimer(TimeSpan.FromSeconds(intervalSeconds));

            // Heartbeat timer: every 5 minutes
            var heartbeatInterval = TimeSpan.FromMinutes(5);
            var lastHeartbeat = DateTime.UtcNow;

            _logger.LogInformation(
                "WORKER STARTED | Polling interval={IntervalSeconds}s, Heartbeat every 5 min. Press Ctrl+C to stop gracefully.",
                intervalSeconds);

            // Run immediately on first iteration (don't wait for timer)
            bool firstRun = true;

            while (!stoppingToken.IsCancellationRequested)
            {
                if (!firstRun)
                {
                    // Emit heartbeat if 5 minutes have passed
                    var now = DateTime.UtcNow;
                    if (now - lastHeartbeat >= heartbeatInterval)
                    {
                        LogHeartbeat();
                        lastHeartbeat = now;
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
                firstRun = false;

                // Check cancellation after wait
                if (stoppingToken.IsCancellationRequested) break;

                var runId = Guid.NewGuid().ToString("N")[..12];
                try
                {
                    using var scope = _scopeFactory.CreateScope();
                    var syncService = scope.ServiceProvider.GetRequiredService<SyncService>();

                    await syncService.SyncOdcanitToMondayAsync(stoppingToken);

                    _totalRunsCompleted++;
                    _lastSyncCompletedAtUtc = DateTime.UtcNow;
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    _logger.LogInformation("Stopping requested... Graceful shutdown in progress.");
                    break;
                }
                catch (Exception ex)
                {
                    _totalFailures++;
                    _logger.LogCritical(ex, "WORKER CRASH during Odcanit->Monday sync. RunId={RunId}", runId);

                    try
                    {
                        await _errorNotifier.NotifyWorkerCrashAsync(runId, ex, stoppingToken);
                    }
                    catch (Exception nex)
                    {
                        _logger.LogWarning(nex, "Error notifier failed during worker crash handling");
                    }
                }
            }

            _logger.LogInformation(
                "WORKER STOPPED | Uptime={UptimeMinutes:F1} min, RunsCompleted={Runs}, TotalFailures={Failures}",
                (DateTime.UtcNow - _workerStartedAtUtc).TotalMinutes,
                _totalRunsCompleted,
                _totalFailures);
        }

        private void LogStartupDiagnostics()
        {
            var env = _hostEnv.EnvironmentName;
            var syncEnabled = _config.GetValue<bool>("Sync:Enabled", true);
            var dryRun = _config.GetValue<bool>("Sync:DryRun", false);
            var intervalSeconds = _config.GetValue<int>("Sync:IntervalSeconds", 1200);
            var maxItems = _config.GetValue<int>("Sync:MaxItemsPerRun", 0);
            var testingEnabled = _config.GetValue<bool>("Testing:Enable", false);
            var odmonTestCasesEnabled = _config.GetValue<bool>("OdmonTestCases:Enable", false);
            var safetyTestMode = _config.GetValue<bool>("Safety:TestMode", false);
            var boardId = _config.GetValue<long>("Monday:BoardId", 0);
            var casesBoardId = _config.GetValue<long>("Monday:CasesBoardId", 0);
            var allowListEnabled = _config.GetValue<bool>("OdcanitLoad:EnableAllowList", false);

            _logger.LogInformation("══════════════════════════════════════════════════════════════");
            _logger.LogInformation("  ODMON Worker Startup Diagnostics");
            _logger.LogInformation("══════════════════════════════════════════════════════════════");
            _logger.LogInformation("  Environment:           {Env}", env);
            _logger.LogInformation("  Sync.Enabled:          {SyncEnabled}", syncEnabled);
            _logger.LogInformation("  Sync.DryRun:           {DryRun}", dryRun);
            _logger.LogInformation("  Sync.IntervalSeconds:  {Interval}", intervalSeconds);
            _logger.LogInformation("  Sync.MaxItemsPerRun:   {Max} (0=unlimited)", maxItems);
            _logger.LogInformation("  Monday.BoardId:        {BoardId}", boardId);
            _logger.LogInformation("  Monday.CasesBoardId:   {CasesBoardId}", casesBoardId);
            _logger.LogInformation("  OdcanitLoad.AllowList: {AllowList}", allowListEnabled);
            _logger.LogInformation("  Testing.Enable:        {Testing}", testingEnabled);
            _logger.LogInformation("  OdmonTestCases.Enable: {TestCases}", odmonTestCasesEnabled);
            _logger.LogInformation("  Safety.TestMode:       {SafetyTest}", safetyTestMode);
            _logger.LogInformation("══════════════════════════════════════════════════════════════");

            // Loud warnings
            if (dryRun)
            {
                _logger.LogWarning("*** DRY RUN MODE IS ON — no changes will be sent to Monday. Set Sync:DryRun=false to sync for real. ***");
            }

            if (testingEnabled)
            {
                _logger.LogWarning("*** TESTING MODE IS ON — the worker may use mock/guard readers. Set Testing:Enable=false for production. ***");
            }

            if (odmonTestCasesEnabled)
            {
                _logger.LogWarning("*** OdmonTestCases IS ON — the worker loads synthetic test data. Set OdmonTestCases:Enable=false for production. ***");
            }

            if (safetyTestMode)
            {
                _logger.LogWarning("*** Safety.TestMode IS ON — production safety features may be bypassed. ***");
            }

            if (boardId == 0)
            {
                _logger.LogError("*** Monday:BoardId is 0 or missing. The worker will fail. Check configuration. ***");
            }

            if (!testingEnabled && !odmonTestCasesEnabled && !dryRun && !safetyTestMode)
            {
                _logger.LogInformation("MODE: PRODUCTION — real Odcanit cases will be synced to Monday.");
            }
        }

        private void LogHeartbeat()
        {
            var uptime = DateTime.UtcNow - _workerStartedAtUtc;
            _logger.LogInformation(
                "HEARTBEAT | Worker alive for {UptimeMinutes:F0} min | Runs={Runs} | LastSync={LastSync:O} | TotalFailures={Failures}",
                uptime.TotalMinutes,
                _totalRunsCompleted,
                _lastSyncCompletedAtUtc == default ? "<none>" : _lastSyncCompletedAtUtc.ToString("O"),
                _totalFailures);
        }
    }
}
