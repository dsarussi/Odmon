using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Odmon.Worker.Services;

namespace Odmon.Worker.Workers
{
    /// <summary>
    /// Runs nearest-hearing reconciliation on a fixed Israel-time schedule,
    /// independently of the ActionLog-driven case sync.
    /// </summary>
    public class HearingNearestScheduleWorker : BackgroundService
    {
        private static readonly TimeZoneInfo IsraelTimeZone = TimeZoneInfo.FindSystemTimeZoneById("Israel Standard Time");
        private static readonly TimeOnly[] DefaultRunTimes =
        [
            new(7, 0),
            new(11, 0),
            new(15, 0),
            new(19, 0),
            new(23, 0)
        ];

        private readonly IServiceScopeFactory _scopeFactory;
        private readonly IConfiguration _configuration;
        private readonly WorkerCoordinator _coordinator;
        private readonly ILogger<HearingNearestScheduleWorker> _logger;

        public HearingNearestScheduleWorker(
            IServiceScopeFactory scopeFactory,
            IConfiguration configuration,
            WorkerCoordinator coordinator,
            ILogger<HearingNearestScheduleWorker> logger)
        {
            _scopeFactory = scopeFactory;
            _configuration = configuration;
            _coordinator = coordinator;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            if (!_configuration.GetValue<bool>("HearingNearestSchedule:Enabled", false))
            {
                _logger.LogInformation("HearingNearestScheduleWorker is disabled (HearingNearestSchedule:Enabled=false).");
                return;
            }

            var runTimes = GetConfiguredRunTimes(_configuration).ToArray();
            var boardId = GetBoardId(_configuration);
            if (boardId <= 0)
            {
                _logger.LogError(
                    "HearingNearestScheduleWorker disabled for this process: no board id configured. Set HearingNearestSchedule:BoardId or Monday:CasesBoardId.");
                return;
            }

            _logger.LogInformation(
                "HearingNearestScheduleWorker started. BoardId={BoardId}, IsraelTimes=[{Times}], OdcanitWrites.Enable={EnableWrites}, OdcanitWrites.DryRun={DryRun}",
                boardId,
                string.Join(", ", runTimes.Select(t => t.ToString("HH:mm"))),
                _configuration.GetValue<bool>("OdcanitWrites:Enable", false),
                _configuration.GetValue<bool>("OdcanitWrites:DryRun", true));

            while (!stoppingToken.IsCancellationRequested)
            {
                var nowUtc = DateTimeOffset.UtcNow;
                var nextUtc = GetNextRunUtc(nowUtc, runTimes, IsraelTimeZone);
                var delay = nextUtc - nowUtc;

                _logger.LogInformation(
                    "HearingNearestScheduleWorker next run at {NextRunIsrael:yyyy-MM-dd HH:mm} Israel time ({NextRunUtc:O}).",
                    TimeZoneInfo.ConvertTime(nextUtc, IsraelTimeZone),
                    nextUtc);

                try
                {
                    await Task.Delay(delay, stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }

                await RunOnceAsync(boardId, stoppingToken);
            }

            _logger.LogInformation("HearingNearestScheduleWorker stopped.");
        }

        private async Task RunOnceAsync(long boardId, CancellationToken ct)
        {
            using var lease = await _coordinator.TryAcquireAsync("HearingNearestScheduleWorker", ct);
            if (lease == null)
            {
                _logger.LogInformation(
                    "COORDINATION | HearingNearestScheduleWorker skipping scheduled run - {ActiveWorker} is active",
                    _coordinator.ActiveWorker ?? "unknown");
                return;
            }

            try
            {
                using var scope = _scopeFactory.CreateScope();
                var service = scope.ServiceProvider.GetRequiredService<HearingNearestSyncService>();
                await service.SyncNearestHearingsAsync(boardId, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "HearingNearestScheduleWorker run failed.");
            }
        }

        internal static DateTimeOffset GetNextRunUtc(
            DateTimeOffset nowUtc,
            IReadOnlyCollection<TimeOnly> runTimes,
            TimeZoneInfo timeZone)
        {
            if (runTimes.Count == 0)
            {
                runTimes = DefaultRunTimes;
            }

            var nowLocal = TimeZoneInfo.ConvertTime(nowUtc, timeZone);
            var today = DateOnly.FromDateTime(nowLocal.DateTime);

            foreach (var runTime in runTimes.OrderBy(t => t))
            {
                var candidateLocal = today.ToDateTime(runTime);
                if (candidateLocal > nowLocal.DateTime)
                {
                    return ToUtc(candidateLocal, timeZone);
                }
            }

            var tomorrowFirst = today.AddDays(1).ToDateTime(runTimes.OrderBy(t => t).First());
            return ToUtc(tomorrowFirst, timeZone);
        }

        private static DateTimeOffset ToUtc(DateTime localUnspecified, TimeZoneInfo timeZone)
        {
            var unspecified = DateTime.SpecifyKind(localUnspecified, DateTimeKind.Unspecified);
            return new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(unspecified, timeZone), TimeSpan.Zero);
        }

        private static IReadOnlyList<TimeOnly> GetConfiguredRunTimes(IConfiguration configuration)
        {
            var configured = configuration
                .GetSection("HearingNearestSchedule:TimesIsrael")
                .Get<string[]>();

            if (configured == null || configured.Length == 0)
            {
                return DefaultRunTimes;
            }

            var parsed = configured
                .Select(value => TimeOnly.TryParse(value, out var time) ? time : (TimeOnly?)null)
                .Where(time => time.HasValue)
                .Select(time => time!.Value)
                .Distinct()
                .OrderBy(time => time)
                .ToArray();

            return parsed.Length == 0 ? DefaultRunTimes : parsed;
        }

        private static long GetBoardId(IConfiguration configuration)
        {
            var configuredBoardId = configuration.GetValue<long>("HearingNearestSchedule:BoardId", 0);
            if (configuredBoardId > 0)
            {
                return configuredBoardId;
            }

            var casesBoardId = configuration.GetValue<long>("Monday:CasesBoardId", 0);
            if (casesBoardId > 0)
            {
                return casesBoardId;
            }

            return configuration.GetValue<long>("Monday:BoardId", 0);
        }
    }
}
