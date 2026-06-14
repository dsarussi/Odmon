using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Odmon.Worker.Configuration;
using Odmon.Worker.Data;
using Odmon.Worker.Models;
using Odmon.Worker.OdcanitAccess;

namespace Odmon.Worker.Services
{
    public sealed class EmailAutomationService
    {
        internal const string AllowedTestRecipient = "odmon@ezer-law.com";
        internal const string AmirEmail = "amir@ezer-law.com";
        internal const string YonatanEmail = "yonatan@ezer-law.com";
        internal const string EdenEmail = "eden@ezer-law.com";
        private static readonly HashSet<int> AmirClientNumbers = [5, 8, 23, 253, 101, 3];
        private static readonly HashSet<int> YonatanClientNumbers = [2, 15];

        private readonly IntegrationDbContext _db;
        private readonly IEmailAutomationGraphClient _graphClient;
        private readonly IEmailAutomationCaseResolver _caseResolver;
        private readonly EmailAutomationSettings _settings;
        private readonly ILogger<EmailAutomationService> _logger;
        private readonly TimeProvider _timeProvider;

        public EmailAutomationService(
            IntegrationDbContext db,
            IEmailAutomationGraphClient graphClient,
            IEmailAutomationCaseResolver caseResolver,
            IOptions<EmailAutomationSettings> options,
            ILogger<EmailAutomationService> logger,
            TimeProvider timeProvider)
        {
            _db = db;
            _graphClient = graphClient;
            _caseResolver = caseResolver;
            _settings = options.Value;
            _logger = logger;
            _timeProvider = timeProvider;
        }

        public async Task RunAsync(CancellationToken cancellationToken)
        {
            foreach (var mailbox in _settings.Mailboxes)
            {
                if (string.IsNullOrWhiteSpace(mailbox.Address))
                {
                    continue;
                }

                await ProcessMailboxAsync(mailbox, cancellationToken);
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

            var state = await _db.EmailAutomationMailboxStates
                .SingleOrDefaultAsync(
                    x => x.Mailbox == normalizedMailbox && x.FolderId == folderId,
                    cancellationToken);

            if (state == null)
            {
                var processingFromUtc =
                    NormalizeUtc(_settings.StartProcessingFromUtc ?? now);
                state = new EmailAutomationMailboxState
                {
                    Mailbox = normalizedMailbox,
                    FolderId = folderId,
                    ProcessingFromUtc = processingFromUtc,
                    CreatedAtUtc = now,
                    UpdatedAtUtc = now
                };
                _db.EmailAutomationMailboxStates.Add(state);
                await _db.SaveChangesAsync(cancellationToken);

                _logger.LogInformation(
                    "EMAILAUTOMATION initialized mailbox delta baseline. Mailbox={Mailbox}, Folder={Folder}, ProcessingFromUtc={ProcessingFromUtc}",
                    normalizedMailbox,
                    folderId,
                    processingFromUtc);
            }

            var nextLink = state.DeltaLink;
            string? completedDeltaLink = null;
            var messagesProcessed = 0;
            var forwardsThisCycle = 0;
            var cycleLimitReached = false;

            do
            {
                var page = await _graphClient.GetDeltaPageAsync(
                    normalizedMailbox,
                    folderId,
                    nextLink,
                    state.ProcessingFromUtc,
                    Math.Max(1, _settings.MaxMessagesPerCycle),
                    cancellationToken);

                foreach (var message in page.Messages)
                {
                    if (message.Removed ||
                        message.ReceivedDateTimeUtc == null ||
                        message.ReceivedDateTimeUtc.Value <= state.ProcessingFromUtc)
                    {
                        continue;
                    }

                    if (messagesProcessed >= Math.Max(1, _settings.MaxMessagesPerCycle) ||
                        ForwardLimitWouldDefer(mailbox.Rules, message, forwardsThisCycle))
                    {
                        cycleLimitReached = true;
                        break;
                    }

                    forwardsThisCycle += await ProcessMessageAsync(
                        normalizedMailbox,
                        mailbox.Rules,
                        message,
                        forwardsThisCycle,
                        cancellationToken);
                    messagesProcessed++;
                }

                if (cycleLimitReached)
                {
                    break;
                }

                nextLink = page.NextLink;
                completedDeltaLink = page.DeltaLink;
            }
            while (!string.IsNullOrWhiteSpace(nextLink));

            if (cycleLimitReached)
            {
                _logger.LogInformation(
                    "EMAILAUTOMATION cycle limit reached. Mailbox={Mailbox}, MessagesProcessed={MessagesProcessed}, Forwards={Forwards}. Delta state was not advanced.",
                    normalizedMailbox,
                    messagesProcessed,
                    forwardsThisCycle);
                return;
            }

            if (string.IsNullOrWhiteSpace(completedDeltaLink))
            {
                throw new InvalidOperationException(
                    $"EMAILAUTOMATION Graph delta sequence for {normalizedMailbox} completed without an @odata.deltaLink.");
            }

            state.DeltaLink = completedDeltaLink;
            state.LastSuccessfulSyncUtc = UtcNow();
            state.UpdatedAtUtc = state.LastSuccessfulSyncUtc.Value;
            await _db.SaveChangesAsync(cancellationToken);

            _logger.LogInformation(
                "EMAILAUTOMATION mailbox cycle completed. Mailbox={Mailbox}, MessagesProcessed={MessagesProcessed}, Forwards={Forwards}",
                normalizedMailbox,
                messagesProcessed,
                forwardsThisCycle);
        }

        private async Task<int> ProcessMessageAsync(
            string mailbox,
            IReadOnlyList<EmailAutomationRuleSettings> rules,
            EmailAutomationMessage message,
            int forwardsThisCycle,
            CancellationToken cancellationToken)
        {
            var ruleMatches = rules
                .Where(x => x.Enabled)
                .Select(x => new { Rule = x, Match = MatchSubject(x.SubjectRegex, message.Subject) })
                .Where(x => x.Match.Success)
                .ToArray();
            var detectedCaseNumber = ruleMatches.FirstOrDefault()?.Match.Value;

            _logger.LogInformation(
                "EMAILAUTOMATION message read. Mailbox={Mailbox}, Sender={Sender}, Subject={Subject}, ReceivedDateTime={ReceivedDateTime}, DetectedCourtCaseNumber={DetectedCourtCaseNumber}",
                mailbox,
                message.Sender,
                message.Subject,
                message.ReceivedDateTimeUtc,
                detectedCaseNumber);

            var alreadyProcessed = await _db.EmailAutomationLogs.AnyAsync(
                x => x.Mailbox == mailbox &&
                     x.GraphMessageId == message.Id &&
                     x.Action != EmailAutomationActions.SkippedAlreadyProcessed,
                cancellationToken);
            if (alreadyProcessed)
            {
                AddAudit(mailbox, null, message, null, null,
                    EmailAutomationActions.SkippedAlreadyProcessed);
                await _db.SaveChangesAsync(cancellationToken);
                return 0;
            }

            AddAudit(mailbox, null, message, detectedCaseNumber, null, EmailAutomationActions.Read);

            if (ruleMatches.Length == 0)
            {
                await _db.SaveChangesAsync(cancellationToken);
                return 0;
            }

            var caseMatch = await _caseResolver.ResolveByCourtCaseNumberAsync(
                detectedCaseNumber!,
                cancellationToken);
            if (caseMatch == null)
            {
                foreach (var item in ruleMatches)
                {
                    AddAudit(
                        mailbox,
                        item.Rule.Name,
                        message,
                        item.Match.Value,
                        null,
                        EmailAutomationActions.NoCaseMatch,
                        $"No Odcanit case was found for court proceeding number {item.Match.Value}.");
                }

                _logger.LogWarning(
                    "EMAILAUTOMATION no Odcanit case match. Mailbox={Mailbox}, DetectedCourtCaseNumber={DetectedCourtCaseNumber}",
                    mailbox,
                    detectedCaseNumber);
                await _db.SaveChangesAsync(cancellationToken);
                return 0;
            }

            var clientNumber = ParseClientNumber(caseMatch.ClientVisualId);
            if (!clientNumber.HasValue)
            {
                foreach (var item in ruleMatches)
                {
                    AddAudit(
                        mailbox,
                        item.Rule.Name,
                        message,
                        item.Match.Value,
                        null,
                        EmailAutomationActions.NoClientMatch,
                        $"Odcanit case {caseMatch.TikCounter} has no resolvable client number.",
                        resolution: new RoutingResolution(caseMatch, null, null));
                }

                _logger.LogWarning(
                    "EMAILAUTOMATION case has no client match. Mailbox={Mailbox}, DetectedCourtCaseNumber={DetectedCourtCaseNumber}, TikCounter={TikCounter}, TikNumber={TikNumber}, ClientVisualId={ClientVisualId}",
                    mailbox,
                    detectedCaseNumber,
                    caseMatch.TikCounter,
                    caseMatch.TikNumber,
                    caseMatch.ClientVisualId);
                await _db.SaveChangesAsync(cancellationToken);
                return 0;
            }

            var resolvedTargetEmail = ResolveTargetEmail(clientNumber.Value);
            var resolution = new RoutingResolution(caseMatch, clientNumber, resolvedTargetEmail);
            _logger.LogInformation(
                "EMAILAUTOMATION routing resolved. Mailbox={Mailbox}, DetectedCourtCaseNumber={DetectedCourtCaseNumber}, ResolvedTikCounter={ResolvedTikCounter}, ResolvedTikNumber={ResolvedTikNumber}, ResolvedClientNumber={ResolvedClientNumber}, ResolvedTargetEmail={ResolvedTargetEmail}",
                mailbox,
                detectedCaseNumber,
                caseMatch.TikCounter,
                caseMatch.TikNumber,
                clientNumber,
                resolvedTargetEmail);

            var forwarded = 0;
            foreach (var item in ruleMatches)
            {
                var rule = item.Rule;
                AddAudit(
                    mailbox,
                    rule.Name,
                    message,
                    item.Match.Value,
                    rule.TestForwardTo,
                    EmailAutomationActions.Matched,
                    resolution: resolution);

                if (!rule.TestForwardEnabled)
                {
                    continue;
                }

                if (_settings.DryRun)
                {
                    AddAudit(
                        mailbox,
                        rule.Name,
                        message,
                        item.Match.Value,
                        rule.TestForwardTo,
                        EmailAutomationActions.DryRunWouldForward,
                        resolution: resolution);
                    continue;
                }

                if (forwardsThisCycle + forwarded >= Math.Max(0, _settings.MaxForwardsPerCycle))
                {
                    continue;
                }

                if (!string.Equals(
                    rule.TestForwardTo?.Trim(),
                    AllowedTestRecipient,
                    StringComparison.OrdinalIgnoreCase))
                {
                    AddAudit(
                        mailbox,
                        rule.Name,
                        message,
                        item.Match.Value,
                        rule.TestForwardTo,
                        EmailAutomationActions.Failed,
                        "Test forwarding target is not on the phase-one allowlist.",
                        resolution: resolution);
                    continue;
                }

                if (string.IsNullOrWhiteSpace(message.InternetMessageId))
                {
                    AddAudit(
                        mailbox,
                        rule.Name,
                        message,
                        item.Match.Value,
                        rule.TestForwardTo,
                        EmailAutomationActions.Failed,
                        "InternetMessageId is required for forwarding idempotency.",
                        resolution: resolution);
                    continue;
                }

                var idempotencyKey = CreateIdempotencyKey(
                    message.InternetMessageId,
                    rule.Name,
                    AllowedTestRecipient);
                if (await _db.EmailAutomationLogs.AnyAsync(
                    x => x.IdempotencyKey == idempotencyKey,
                    cancellationToken))
                {
                    AddAudit(
                        mailbox,
                        rule.Name,
                        message,
                        item.Match.Value,
                        AllowedTestRecipient,
                        EmailAutomationActions.SkippedAlreadyProcessed,
                        resolution: resolution);
                    continue;
                }

                var forwardAudit = AddAudit(
                    mailbox,
                    rule.Name,
                    message,
                    item.Match.Value,
                    AllowedTestRecipient,
                    EmailAutomationActions.ForwardedToTestMailbox,
                    idempotencyKey: idempotencyKey,
                    resolution: resolution,
                    actualForwardTo: AllowedTestRecipient);

                // Reserve the unique key before the external side effect. This deliberately
                // provides at-most-once forwarding if Graph's response is ambiguous.
                await _db.SaveChangesAsync(cancellationToken);
                try
                {
                    _logger.LogInformation(
                        "EMAILAUTOMATION forwarding to test mailbox. Mailbox={Mailbox}, DetectedCourtCaseNumber={DetectedCourtCaseNumber}, ResolvedTikCounter={ResolvedTikCounter}, ResolvedTikNumber={ResolvedTikNumber}, ResolvedClientNumber={ResolvedClientNumber}, ResolvedTargetEmail={ResolvedTargetEmail}, ActualForwardTo={ActualForwardTo}",
                        mailbox,
                        item.Match.Value,
                        caseMatch.TikCounter,
                        caseMatch.TikNumber,
                        clientNumber,
                        resolvedTargetEmail,
                        AllowedTestRecipient);
                    await _graphClient.ForwardMessageAsync(
                        mailbox,
                        message.Id,
                        AllowedTestRecipient,
                        cancellationToken);
                    forwardAudit.ProcessedAtUtc = UtcNow();
                    forwarded++;
                }
                catch (GraphThrottledException ex)
                {
                    forwardAudit.Action = EmailAutomationActions.Failed;
                    forwardAudit.ErrorMessage = Truncate(ex.Message, 2000);
                    await _db.SaveChangesAsync(cancellationToken);
                    throw;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    forwardAudit.Action = EmailAutomationActions.Failed;
                    forwardAudit.ErrorMessage = Truncate(ex.Message, 2000);
                    _logger.LogError(
                        ex,
                        "EMAILAUTOMATION test forward failed. Mailbox={Mailbox}, GraphMessageId={GraphMessageId}, RuleName={RuleName}",
                        mailbox,
                        message.Id,
                        rule.Name);
                }
            }

            await _db.SaveChangesAsync(cancellationToken);
            return forwarded;
        }

        internal static int? ParseClientNumber(string? clientVisualId)
        {
            var parsed = DocumentTypeMap.ParseClientNumber(clientVisualId, '\\');
            return parsed ?? DocumentTypeMap.ParseClientNumber(clientVisualId, '/');
        }

        internal static string ResolveTargetEmail(int clientNumber)
        {
            if (AmirClientNumbers.Contains(clientNumber))
            {
                return AmirEmail;
            }

            return YonatanClientNumbers.Contains(clientNumber)
                ? YonatanEmail
                : EdenEmail;
        }

        private bool ForwardLimitWouldDefer(
            IReadOnlyList<EmailAutomationRuleSettings> rules,
            EmailAutomationMessage message,
            int forwardsThisCycle)
        {
            if (_settings.DryRun ||
                forwardsThisCycle < Math.Max(0, _settings.MaxForwardsPerCycle))
            {
                return false;
            }

            return rules.Any(
                rule => rule.Enabled &&
                        rule.TestForwardEnabled &&
                        string.Equals(
                            rule.TestForwardTo?.Trim(),
                            AllowedTestRecipient,
                            StringComparison.OrdinalIgnoreCase) &&
                        MatchSubject(rule.SubjectRegex, message.Subject).Success);
        }

        internal static Match MatchSubject(string pattern, string? subject)
        {
            if (string.IsNullOrWhiteSpace(pattern) || string.IsNullOrWhiteSpace(subject))
            {
                return Match.Empty;
            }

            return Regex.Match(
                subject,
                pattern,
                RegexOptions.CultureInvariant,
                TimeSpan.FromSeconds(1));
        }

        internal static string CreateIdempotencyKey(
            string internetMessageId,
            string ruleName,
            string targetEmail)
        {
            var value = string.Join(
                "\n",
                internetMessageId.Trim(),
                ruleName.Trim().ToLowerInvariant(),
                targetEmail.Trim().ToLowerInvariant());
            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
        }

        private EmailAutomationLog AddAudit(
            string mailbox,
            string? ruleName,
            EmailAutomationMessage message,
            string? detectedCaseNumber,
            string? targetEmail,
            string action,
            string? errorMessage = null,
            string? idempotencyKey = null,
            RoutingResolution? resolution = null,
            string? actualForwardTo = null)
        {
            var audit = new EmailAutomationLog
            {
                Mailbox = mailbox,
                RuleName = ruleName,
                InternetMessageId = message.InternetMessageId,
                GraphMessageId = message.Id,
                Subject = Truncate(message.Subject, 1000),
                Sender = Truncate(message.Sender, 320),
                ReceivedDateTimeUtc = message.ReceivedDateTimeUtc,
                DetectedCourtCaseNumber = detectedCaseNumber,
                ResolvedTikCounter = resolution?.CaseMatch.TikCounter,
                ResolvedTikNumber = resolution?.CaseMatch.TikNumber,
                ResolvedClientNumber = resolution?.ClientNumber,
                ResolvedTargetEmail = resolution?.ResolvedTargetEmail,
                ActualForwardTo = actualForwardTo,
                TargetEmail = targetEmail,
                Action = action,
                IdempotencyKey = idempotencyKey,
                ErrorMessage = Truncate(errorMessage, 2000),
                CreatedAtUtc = UtcNow()
            };
            _db.EmailAutomationLogs.Add(audit);
            return audit;
        }

        private DateTime UtcNow() => _timeProvider.GetUtcNow().UtcDateTime;

        private static DateTime NormalizeUtc(DateTime value)
            => value.Kind == DateTimeKind.Unspecified
                ? DateTime.SpecifyKind(value, DateTimeKind.Utc)
                : value.ToUniversalTime();

        private static string? Truncate(string? value, int maxLength)
            => value?.Length > maxLength ? value[..maxLength] : value;

        private sealed record RoutingResolution(
            EmailAutomationCaseMatch CaseMatch,
            int? ClientNumber,
            string? ResolvedTargetEmail);
    }
}
