using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Odmon.Worker.Configuration;
using Odmon.Worker.Services;

namespace Odmon.Worker.Workers
{
    public sealed class EmailAutomationWorker : BackgroundService
    {
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly EmailAutomationSettings _settings;
        private readonly ILogger<EmailAutomationWorker> _logger;

        public EmailAutomationWorker(
            IServiceScopeFactory scopeFactory,
            IOptions<EmailAutomationSettings> options,
            ILogger<EmailAutomationWorker> logger)
        {
            _scopeFactory = scopeFactory;
            _settings = options.Value;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            if (!_settings.Enabled)
            {
                _logger.LogInformation(
                    "EMAILAUTOMATION worker disabled (EmailAutomation:Enabled=false).");
                return;
            }

            ValidateConfiguration();
            var interval = TimeSpan.FromMinutes(Math.Max(1, _settings.IntervalMinutes));
            _logger.LogInformation(
                "EMAILAUTOMATION worker started. IntervalMinutes={IntervalMinutes}, DryRun={DryRun}, MailboxCount={MailboxCount}",
                interval.TotalMinutes,
                _settings.DryRun,
                _settings.Mailboxes.Count);

            using var timer = new PeriodicTimer(interval);
            var runImmediately = true;
            while (!stoppingToken.IsCancellationRequested)
            {
                if (!runImmediately)
                {
                    try
                    {
                        if (!await timer.WaitForNextTickAsync(stoppingToken))
                        {
                            break;
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                }
                runImmediately = false;

                try
                {
                    using var scope = _scopeFactory.CreateScope();
                    var service = scope.ServiceProvider.GetRequiredService<EmailAutomationService>();
                    await service.RunAsync(stoppingToken);
                }
                catch (GraphThrottledException ex)
                {
                    _logger.LogWarning(
                        "EMAILAUTOMATION Graph throttled this cycle. RetryAfter={RetryAfter}. The delta state was not advanced; retrying on the next scheduled run.",
                        ex.RetryAfter);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(
                        "EMAILAUTOMATION cycle failed. ErrorCategory={ErrorCategory}",
                        ex.GetType().Name);
                }
            }

            _logger.LogInformation("EMAILAUTOMATION worker stopped.");
        }

        private void ValidateConfiguration()
        {
            if (string.IsNullOrWhiteSpace(_settings.TenantId) ||
                string.IsNullOrWhiteSpace(_settings.ClientId) ||
                string.IsNullOrWhiteSpace(_settings.ClientSecret) ||
                string.IsNullOrWhiteSpace(_settings.FingerprintKey))
            {
                throw new InvalidOperationException(
                    "EMAILAUTOMATION TenantId, ClientId, ClientSecret, and FingerprintKey are required when enabled.");
            }

            if (_settings.Mailboxes.Count == 0)
            {
                throw new InvalidOperationException(
                    "EMAILAUTOMATION at least one mailbox must be configured when enabled.");
            }
        }
    }
}
