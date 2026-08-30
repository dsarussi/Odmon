using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Odmon.Worker.Configuration;
using Odmon.Worker.Services;

namespace Odmon.Worker.Workers
{
    public sealed class EmailFilingWorker : BackgroundService
    {
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly EmailFilingSettings _settings;
        private readonly EmailAutomationSettings _automationSettings;
        private readonly ILogger<EmailFilingWorker> _logger;

        public EmailFilingWorker(
            IServiceScopeFactory scopeFactory,
            IOptions<EmailFilingSettings> options,
            IOptions<EmailAutomationSettings> automationOptions,
            ILogger<EmailFilingWorker> logger)
        {
            _scopeFactory = scopeFactory;
            _settings = options.Value;
            _automationSettings = automationOptions.Value;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            if (!_settings.Enabled)
            {
                _logger.LogInformation("EMAILFILING worker disabled (EmailFiling:Enabled=false).");
                return;
            }

            ValidateConfiguration();
            var interval = TimeSpan.FromMinutes(Math.Max(1, _settings.IntervalMinutes));
            _logger.LogInformation(
                "EMAILFILING worker started. IntervalMinutes={IntervalMinutes}, DryRun={DryRun}, RealWriteEnabled={RealWriteEnabled}, MailboxCount={MailboxCount}",
                interval.TotalMinutes,
                _settings.DryRun,
                _settings.RealWriteEnabled,
                _automationSettings.Mailboxes.Count);

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
                    var service = scope.ServiceProvider.GetRequiredService<EmailFilingPollingService>();
                    await service.RunAsync(stoppingToken);
                }
                catch (GraphThrottledException ex)
                {
                    _logger.LogWarning(
                        "EMAILFILING Graph throttled this cycle. RetryAfter={RetryAfter}. Filing delta state was not advanced.",
                        ex.RetryAfter);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (InvalidDataException ex) when (
                    MicrosoftGraphEmailAutomationClient.TryGetCursorValidationReason(ex, out _))
                {
                    _ = MicrosoftGraphEmailAutomationClient.TryGetCursorValidationReason(
                        ex,
                        out var reason);
                    _logger.LogError(
                        "EMAILFILING cycle failed. Filing delta state was not advanced. ErrorCategory={ErrorCategory}, CursorValidationReason={CursorValidationReason}",
                        nameof(InvalidDataException),
                        reason);
                }
                catch (Exception ex)
                {
                    _logger.LogError(
                        "EMAILFILING cycle failed. Filing delta state was not advanced. ErrorCategory={ErrorCategory}",
                        ex.GetType().Name);
                }
            }

            _logger.LogInformation("EMAILFILING worker stopped.");
        }

        private void ValidateConfiguration()
        {
            if (string.IsNullOrWhiteSpace(_automationSettings.TenantId) ||
                string.IsNullOrWhiteSpace(_automationSettings.ClientId) ||
                string.IsNullOrWhiteSpace(_automationSettings.ClientSecret) ||
                string.IsNullOrWhiteSpace(_automationSettings.FingerprintKey))
            {
                throw new InvalidOperationException(
                    "EmailAutomation TenantId, ClientId, ClientSecret, and FingerprintKey are required when EmailFiling is enabled.");
            }

            if (_automationSettings.Mailboxes.Count == 0)
            {
                throw new InvalidOperationException(
                    "EmailAutomation must configure at least one mailbox when EmailFiling is enabled.");
            }

            if (_settings.MaxMimeMessageBytes <= 0 ||
                _settings.MaxDeltaPageBytes <= 0 ||
                _settings.MaxMimeAttachmentCount <= 0 ||
                _settings.MaxIdentifierCandidates <= 0)
            {
                throw new InvalidOperationException(
                    "EmailFiling MIME/page size, attachment-count, and identifier-count limits must be positive.");
            }

            if (!_settings.DryRun && _settings.RealWriteEnabled)
            {
                if (!_settings.IsRealWriteAuthorityConfigurationValid())
                {
                    throw new InvalidOperationException(
                        "EmailFiling real-write authority configuration is invalid.");
                }

                if (!_settings.IsDestinationRootConfigurationValid())
                {
                    throw new InvalidOperationException(
                        "EmailFiling allowed destination roots are empty or invalid.");
                }
            }
        }
    }
}
