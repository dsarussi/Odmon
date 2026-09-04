using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
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
                var configuredStart = _settings.StartProcessingFromUtc.HasValue
                    ? NormalizeUtc(_settings.StartProcessingFromUtc.Value)
                    : now;
                if (configuredStart < now && !_settings.AllowHistoricalBackfill)
                {
                    throw new InvalidOperationException(
                        "EmailFiling historical processing requires EmailFiling:AllowHistoricalBackfill=true.");
                }

                state = new EmailFilingMailboxState
                {
                    Mailbox = normalizedMailbox,
                    FolderId = folderId,
                    ProcessingFromUtc = configuredStart,
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

            // Older versions stored page checkpoints in DeltaLink; those opaque
            // cursors still resume normally and are replaced when the round ends.
            var cursor = state.NextLink ?? state.DeltaLink;
            var processed = 0;
            var unchanged = 0;
            var pages = 0;
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
                pages++;

                foreach (var message in page.Messages)
                {
                    if (message.Removed ||
                        message.ReceivedDateTimeUtc == null ||
                        message.ReceivedDateTimeUtc.Value <= state.ProcessingFromUtc)
                    {
                        continue;
                    }

                    var messageFingerprint = _filingService.CreateMessageFingerprint(normalizedMailbox, message);
                    var contentFingerprint = CreateContentFingerprint(message, _automationSettings.FingerprintKey);
                    var lastProcessedContent = await _db.EmailFilingDiagnostics
                        .Where(row => row.Mailbox == normalizedMailbox &&
                                      row.MessageFingerprint == messageFingerprint &&
                                      row.ProcessedContentFingerprint != null)
                        .OrderByDescending(row => row.Id)
                        .Select(row => row.ProcessedContentFingerprint)
                        .FirstOrDefaultAsync(cancellationToken);
                    if (string.Equals(lastProcessedContent, contentFingerprint, StringComparison.Ordinal))
                    {
                        unchanged++;
                        continue;
                    }

                    var diagnostic = await _filingService.ProcessAsync(
                        normalizedMailbox,
                        mailbox.Rules,
                        message,
                        cancellationToken);
                    if (diagnostic != null)
                    {
                        // Persist each completed message independently of the
                        // round. A later page/message failure must retry without
                        // resolving this unchanged successful prefix again.
                        diagnostic.ProcessedContentFingerprint = contentFingerprint;
                        await _db.SaveChangesAsync(cancellationToken);
                    }
                    processed++;
                }

                if (!string.IsNullOrWhiteSpace(page.NextLink))
                {
                    if (processed >= maxMessages)
                    {
                        // A nextLink is a safe page-boundary checkpoint. This
                        // avoids replaying a full first page forever when a delta
                        // sequence is larger than the per-cycle limit.
                        state.NextLink = page.NextLink;
                        state.UpdatedAtUtc = UtcNow();
                        await _db.SaveChangesAsync(cancellationToken);
                        _logger.LogInformation(
                            "EMAILFILING cycle limit checkpointed. MessagesProcessed={MessagesProcessed}, UnchangedSkipped={UnchangedSkipped}, Pages={Pages}",
                            processed, unchanged, pages);
                        return;
                    }

                    cursor = page.NextLink;
                    continue;
                }

                if (string.IsNullOrWhiteSpace(page.DeltaLink))
                {
                    throw new InvalidOperationException(
                        "EMAILFILING Graph delta sequence completed without an @odata.deltaLink.");
                }

                state.DeltaLink = page.DeltaLink;
                state.NextLink = null;
                state.LastSuccessfulSyncUtc = UtcNow();
                state.UpdatedAtUtc = state.LastSuccessfulSyncUtc.Value;
                await _db.SaveChangesAsync(cancellationToken);
                _logger.LogInformation(
                    "EMAILFILING mailbox cycle completed. MessagesProcessed={MessagesProcessed}, UnchangedSkipped={UnchangedSkipped}, Pages={Pages}",
                    processed, unchanged, pages);
                return;
            }
        }

        internal static string CreateContentFingerprint(EmailAutomationMessage message, string fingerprintKey)
        {
            if (string.IsNullOrWhiteSpace(fingerprintKey))
                throw new InvalidOperationException("EmailAutomation:FingerprintKey is required when EmailFiling is enabled.");

            // Compare the inputs already supplied to filing. Graph IDs, read
            // flags, change keys and modification times are not content changes.
            // JSON preserves field boundaries; only the keyed digest is stored.
            var content = JsonSerializer.SerializeToUtf8Bytes(new
            {
                Version = "email-filing-content-v1",
                message.Subject,
                message.Sender,
                message.Body,
                message.BodyContentType,
                To = OrderedRecipients(message.ToRecipients),
                Cc = OrderedRecipients(message.CcRecipients),
                Bcc = OrderedRecipients(message.BccRecipients),
                message.ReceivedDateTimeUtc,
                message.SentDateTimeUtc
            });
            return Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(fingerprintKey), content));
        }

        private static string[] OrderedRecipients(IReadOnlyList<string>? recipients)
            => recipients?.OrderBy(value => value, StringComparer.Ordinal).ToArray() ?? [];

        private DateTime UtcNow() => _timeProvider.GetUtcNow().UtcDateTime;

        private static DateTime NormalizeUtc(DateTime value)
            => value.Kind == DateTimeKind.Unspecified
                ? DateTime.SpecifyKind(value, DateTimeKind.Utc)
                : value.ToUniversalTime();
    }
}
