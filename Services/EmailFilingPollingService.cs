using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Odmon.Worker.Configuration;
using Odmon.Worker.Data;
using Odmon.Worker.Models;

namespace Odmon.Worker.Services
{
    /// <summary>
    /// Owns EmailFiling's independent Graph delta cursor. Filing failures do not
    /// advance this cursor and cannot delay or change EmailAutomation forwarding.
    /// </summary>
    public sealed class EmailFilingPollingService
    {
        private readonly IntegrationDbContext _db;
        private readonly IEmailFilingGraphClient _graphClient;
        private readonly EmailFilingService _filingService;
        private readonly EmailFilingSettings _settings;
        private readonly EmailAutomationSettings _automationSettings;
        private readonly ILogger<EmailFilingPollingService> _logger;
        private readonly TimeProvider _timeProvider;

        public EmailFilingPollingService(
            IntegrationDbContext db,
            IEmailFilingGraphClient graphClient,
            EmailFilingService filingService,
            IOptions<EmailFilingSettings> options,
            IOptions<EmailAutomationSettings> automationOptions,
            ILogger<EmailFilingPollingService> logger,
            TimeProvider timeProvider)
        {
            _db = db;
            _graphClient = graphClient;
            _filingService = filingService;
            _settings = options.Value;
            _automationSettings = automationOptions.Value;
            _logger = logger;
            _timeProvider = timeProvider;
        }

        public async Task RunAsync(CancellationToken cancellationToken)
        {
            foreach (var mailbox in _automationSettings.Mailboxes)
            {
                if (!string.IsNullOrWhiteSpace(mailbox.Address))
                {
                    await ProcessMailboxAsync(mailbox, cancellationToken);
                }
            }
        }

        internal async Task ProcessMailboxAsync(
            EmailAutomationMailboxSettings mailbox,
            CancellationToken cancellationToken)
        {
            var now = UtcNow();
            var folderId = string.IsNullOrWhiteSpace(mailbox.InboxFolder)
                ? "Inbox"
                : mailbox.InboxFolder.Trim();
            var normalizedMailbox = mailbox.Address.Trim().ToLowerInvariant();
            var state = await _db.EmailFilingMailboxStates.SingleOrDefaultAsync(
                row => row.Mailbox == normalizedMailbox && row.FolderId == folderId,
                cancellationToken);

            if (state == null)
            {
                state = new EmailFilingMailboxState
                {
                    Mailbox = normalizedMailbox,
                    FolderId = folderId,
                    ProcessingFromUtc = NormalizeUtc(_settings.StartProcessingFromUtc ?? now),
                    CreatedAtUtc = now,
                    UpdatedAtUtc = now
                };
                _db.EmailFilingMailboxStates.Add(state);
                await _db.SaveChangesAsync(cancellationToken);
                _logger.LogInformation(
                    "EMAILFILING initialized independent mailbox delta baseline. Folder={Folder}, ProcessingFromUtc={ProcessingFromUtc}",
                    folderId,
                    state.ProcessingFromUtc);
            }

            var cursor = state.DeltaLink;
            var processed = 0;
            var maxMessages = Math.Max(1, _settings.MaxMessagesPerCycle);
            while (true)
            {
                var remaining = Math.Max(1, maxMessages - processed);
                var page = await _graphClient.GetDeltaPageAsync(
                    normalizedMailbox,
                    folderId,
                    cursor,
                    state.ProcessingFromUtc,
                    remaining,
                    cancellationToken);

                foreach (var message in page.Messages)
                {
                    if (message.Removed ||
                        message.ReceivedDateTimeUtc == null ||
                        message.ReceivedDateTimeUtc.Value <= state.ProcessingFromUtc)
                    {
                        continue;
                    }

                    await _filingService.ProcessAsync(
                        normalizedMailbox,
                        mailbox.Rules,
                        message,
                        cancellationToken);
                    processed++;
                }

                if (!string.IsNullOrWhiteSpace(page.NextLink))
                {
                    if (processed >= maxMessages)
                    {
                        // A nextLink is a safe page-boundary checkpoint. This
                        // avoids replaying a full first page forever when a delta
                        // sequence is larger than the per-cycle limit.
                        state.DeltaLink = page.NextLink;
                        state.UpdatedAtUtc = UtcNow();
                        await _db.SaveChangesAsync(cancellationToken);
                        _logger.LogInformation(
                            "EMAILFILING cycle limit checkpointed. MessagesProcessed={MessagesProcessed}",
                            processed);
                        return;
                    }

                    cursor = page.NextLink;
                    continue;
                }

                if (string.IsNullOrWhiteSpace(page.DeltaLink))
                {
                    throw new InvalidOperationException(
                        $"EMAILFILING Graph delta sequence for {normalizedMailbox} completed without an @odata.deltaLink.");
                }

                state.DeltaLink = page.DeltaLink;
                state.LastSuccessfulSyncUtc = UtcNow();
                state.UpdatedAtUtc = state.LastSuccessfulSyncUtc.Value;
                await _db.SaveChangesAsync(cancellationToken);
                _logger.LogInformation(
                    "EMAILFILING mailbox cycle completed. MessagesProcessed={MessagesProcessed}",
                    processed);
                return;
            }
        }

        private DateTime UtcNow() => _timeProvider.GetUtcNow().UtcDateTime;

        private static DateTime NormalizeUtc(DateTime value)
            => value.Kind == DateTimeKind.Unspecified
                ? DateTime.SpecifyKind(value, DateTimeKind.Utc)
                : value.ToUniversalTime();
    }
}
