using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Odmon.Worker.Configuration;
using Odmon.Worker.Security;
using Odmon.Worker.Services;

namespace Odmon.Worker.Workers
{
    public sealed class VoicenterCallSummaryWorker : BackgroundService
    {
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly VoicenterCallSummarySettings _settings;
        private readonly ISecretProvider _secretProvider;
        private readonly ILogger<VoicenterCallSummaryWorker> _logger;

        public VoicenterCallSummaryWorker(
            IServiceScopeFactory scopeFactory,
            IOptions<VoicenterCallSummarySettings> settings,
            ISecretProvider secretProvider,
            ILogger<VoicenterCallSummaryWorker> logger)
        {
            _scopeFactory = scopeFactory;
            _settings = settings.Value;
            _secretProvider = secretProvider;
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
                }

                try { await Task.Delay(interval, stoppingToken); }
                catch (OperationCanceledException) { break; }
            }

            _logger.LogInformation("VOICENTER_WORKER | Stopped");
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
    }
}
