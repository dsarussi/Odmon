using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Odmon.Worker.Configuration;
using Odmon.Worker.Security;
using Odmon.Worker.Services;
using Odmon.Worker.Voicenter;

namespace Odmon.Worker.Workers
{
    public sealed class VoicenterCallSummaryWorker : BackgroundService
    {
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly VoicenterCallSummarySettings _settings;
        private readonly ISecretProvider _secretProvider;
        private readonly IEmailNotifier _emailNotifier;
        private readonly ILogger<VoicenterCallSummaryWorker> _logger;

        private DateTime _lastSuccessfulCycleUtc = DateTime.MinValue;
        private DateTime _lastAlertUtc = DateTime.MinValue;

        /// <summary>Last successful run result, read by daily summary email.</summary>
        private volatile VoicenterRunResult? _lastRunResult;
        public VoicenterRunResult? LastRunResult => _lastRunResult;

        public VoicenterCallSummaryWorker(
            IServiceScopeFactory scopeFactory,
            IOptions<VoicenterCallSummarySettings> settings,
            ISecretProvider secretProvider,
            IEmailNotifier emailNotifier,
            ILogger<VoicenterCallSummaryWorker> logger)
        {
            _scopeFactory = scopeFactory;
            _settings = settings.Value;
            _secretProvider = secretProvider;
            _emailNotifier = emailNotifier;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            if (!_settings.Enabled)
            {
                _logger.LogInformation("VOICENTER_WORKER | Disabled by config");
                return;
            }

            var code = ResolveSecret("Voicenter__Code", _settings.Code);
            var bearerToken = ResolveSecret("Voicenter__BearerToken", _settings.BearerToken);
            if (string.IsNullOrWhiteSpace(code) || string.IsNullOrWhiteSpace(bearerToken))
            {
                _logger.LogError("VOICENTER_WORKER | Missing Voicenter__Code or Voicenter__BearerToken secret; cannot start");
                return;
            }

            var interval = TimeSpan.FromHours(_settings.IntervalHours);
            _logger.LogInformation("VOICENTER_WORKER | Started | Interval={Interval}h, Lookback={Lookback}h, TestMode={TestMode}",
                _settings.IntervalHours, _settings.LookbackHours, _settings.TestMode);

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    using var scope = _scopeFactory.CreateScope();
                    var service = scope.ServiceProvider.GetRequiredService<VoicenterCallSummaryService>();
                    var result = await service.RunAsync(code, bearerToken, stoppingToken);

                    _lastRunResult = result;
                    _lastSuccessfulCycleUtc = DateTime.UtcNow;

                    _logger.LogInformation(
                        "VOICENTER_WORKER | Cycle done | Fetched={Fetched}, Written={Written}, NoAI={NoAi}, NoMatch={NoMatch}, Dup={Dup}, Failed={Failed}",
                        result.Fetched, result.Written, result.SkippedNoAi, result.SkippedNoMatch,
                        result.SkippedDuplicate, result.Failed);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "VOICENTER_WORKER | Unhandled error in cycle");
                    TrySendCycleFailureAlert(ex);
                }

                CheckStaleWorker();

                try { await Task.Delay(interval, stoppingToken); }
                catch (OperationCanceledException) { break; }
            }

            _logger.LogInformation("VOICENTER_WORKER | Stopped");
        }

        private void TrySendCycleFailureAlert(Exception ex)
        {
            if (!_settings.AlertOnUnhandledException) return;
            if (!IsAlertCooldownElapsed()) return;

            _lastAlertUtc = DateTime.UtcNow;
            var body = $"Timestamp: {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC\n"
                     + $"Exception: {ex.GetType().Name}\n"
                     + $"Message: {ex.Message}\n"
                     + $"Stack (top):\n{Truncate(ex.StackTrace, 600)}";
            _emailNotifier.QueueCriticalAlert(
                "VoicenterCallSummaryWorker failed",
                body,
                exceptionType: ex.GetType().Name,
                source: "VoicenterCallSummaryWorker");
        }

        private void CheckStaleWorker()
        {
            if (!_settings.AlertOnStaleWorker) return;
            if (_lastSuccessfulCycleUtc == DateTime.MinValue) return;

            var hoursSinceSuccess = (DateTime.UtcNow - _lastSuccessfulCycleUtc).TotalHours;
            if (hoursSinceSuccess < _settings.StaleWorkerThresholdHours) return;
            if (!IsAlertCooldownElapsed()) return;

            _lastAlertUtc = DateTime.UtcNow;
            var body = $"No successful Voicenter cycle for {hoursSinceSuccess:F1} hours.\n"
                     + $"Last success: {_lastSuccessfulCycleUtc:yyyy-MM-dd HH:mm:ss} UTC\n"
                     + $"Threshold: {_settings.StaleWorkerThresholdHours}h";
            _emailNotifier.QueueCriticalAlert(
                "VoicenterCallSummaryWorker stale — no successful cycle",
                body,
                source: "VoicenterCallSummaryWorker");
            _logger.LogWarning("VOICENTER_WORKER | Stale worker alert sent | HoursSinceSuccess={Hours:F1}",
                hoursSinceSuccess);
        }

        private bool IsAlertCooldownElapsed()
        {
            if (_lastAlertUtc == DateTime.MinValue) return true;
            return (DateTime.UtcNow - _lastAlertUtc).TotalMinutes >= _settings.FailureAlertCooldownMinutes;
        }

        private string? ResolveSecret(string secretKey, string? configFallback)
        {
            var value = _secretProvider.GetSecret(secretKey);
            if (!string.IsNullOrWhiteSpace(value) && !IsPlaceholder(value))
                return value;
            if (!string.IsNullOrWhiteSpace(configFallback) && !IsPlaceholder(configFallback))
            {
                _logger.LogWarning("VOICENTER_WORKER | Secret '{Key}' resolved from config fallback; migrate to secret store", secretKey);
                return configFallback;
            }
            return null;
        }

        private static bool IsPlaceholder(string v) =>
            v.StartsWith("CHANGE_ME", StringComparison.OrdinalIgnoreCase);

        private static string Truncate(string? s, int max)
            => string.IsNullOrEmpty(s) ? "" : (s.Length <= max ? s : s[..max] + "…");
    }
}
