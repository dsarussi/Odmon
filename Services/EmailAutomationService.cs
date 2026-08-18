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
        private static readonly Regex ForwardOrReplyPrefixRegex = new(
            @"(?:^|[\s\[\(])(?:RE|FW|FWD|השב|הועבר)\s*:",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled,
            TimeSpan.FromSeconds(1));

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

                    try
                    {
                        forwardsThisCycle += await ProcessMessageAsync(
                            normalizedMailbox,
                            mailbox.Rules,
                            message,
                            forwardsThisCycle,
                            cancellationToken);
                    }
                    catch (ForwardLimitReachedException)
                    {
                        cycleLimitReached = true;
                        break;
                    }
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

            var messageFingerprint = CreateMessageFingerprint(mailbox, message);

            var alreadyProcessed = await _db.EmailAutomationLogs.AnyAsync(
                x => x.Mailbox == mailbox &&
                     x.GraphMessageId == messageFingerprint,
                cancellationToken);
            if (alreadyProcessed)
            {
                return 0;
            }

            if (ruleMatches.Length == 0)
            {
                return 0;
            }

            var caseMatch = await _caseResolver.ResolveByCourtCaseNumberAsync(
                detectedCaseNumber!,
                cancellationToken);
            if (caseMatch == null || caseMatch.IsAmbiguous)
            {
                _logger.LogWarning(
                    "EMAILAUTOMATION message could not be resolved to one Odcanit case. Mailbox={Mailbox}, ResolutionCategory={ResolutionCategory}",
                    mailbox,
                    caseMatch?.IsAmbiguous == true ? "Ambiguous" : "NotFound");
                return 0;
            }

            var clientNumber = ParseClientNumber(caseMatch.ClientVisualId);
            if (!clientNumber.HasValue)
            {
                foreach (var item in ruleMatches)
                {
                    AddAudit(
                        mailbox,
                        message,
                        EmailAutomationActions.NoClientMatch,
                        "MissingRouting",
                        resolution: new RoutingResolution(caseMatch, null, null));
                }

                _logger.LogWarning(
                    "EMAILAUTOMATION case has no routing target. Mailbox={Mailbox}, TikCounter={TikCounter}",
                    mailbox,
                    caseMatch.TikCounter);
                await _db.SaveChangesAsync(cancellationToken);
                return 0;
            }

            var resolvedTargetEmail = ResolveTargetEmail(clientNumber.Value);
            var resolution = new RoutingResolution(caseMatch, clientNumber, resolvedTargetEmail);
            _logger.LogInformation(
                "EMAILAUTOMATION routing resolved. Mailbox={Mailbox}, ResolvedTikCounter={ResolvedTikCounter}, ResolvedTargetEmail={ResolvedTargetEmail}",
                mailbox,
                caseMatch.TikCounter,
                resolvedTargetEmail);

            var forwarded = 0;
            foreach (var item in ruleMatches)
            {
                var rule = item.Rule;
                AddAudit(
                    mailbox,
                    message,
                    EmailAutomationActions.Matched,
                    resolution: resolution);

                if (rule.TestForwardEnabled)
                {
                    if (_settings.RealForwardEnabled)
                    {
                        AddAudit(
                            mailbox,
                            message,
                            EmailAutomationActions.SkippedRealForwardBecauseTestModeEnabled,
                            "Real forwarding was skipped because test forwarding is enabled.",
                            resolution: resolution);
                        _logger.LogWarning(
                            "EMAILAUTOMATION real forwarding skipped because test mode is enabled. Mailbox={Mailbox}, RuleName={RuleName}, ResolvedTargetEmail={ResolvedTargetEmail}",
                            mailbox,
                            rule.Name,
                            resolvedTargetEmail);
                    }

                    if (_settings.DryRun)
                    {
                        AddAudit(
                            mailbox,
                            message,
                            EmailAutomationActions.DryRunWouldForward,
                            resolution: resolution);
                        continue;
                    }

                    if (!string.Equals(
                        rule.TestForwardTo?.Trim(),
                        AllowedTestRecipient,
                        StringComparison.OrdinalIgnoreCase))
                    {
                        AddAudit(
                            mailbox,
                            message,
                            EmailAutomationActions.Failed,
                            "Test forwarding target is not on the phase-one allowlist.",
                            resolution: resolution);
                        continue;
                    }

                    forwarded += await ForwardAsync(
                        mailbox,
                        rule.Name,
                        message,
                        AllowedTestRecipient,
                        EmailAutomationActions.ForwardedToTestMailbox,
                        resolution,
                        forwardsThisCycle + forwarded,
                        "test",
                        cancellationToken);
                    continue;
                }

                if (!_settings.RealForwardEnabled)
                {
                    continue;
                }

                if (_settings.DryRun)
                {
                    AddAudit(
                        mailbox,
                        message,
                        EmailAutomationActions.DryRunWouldForward,
                        resolution: resolution);
                    continue;
                }

                if (EmailEquals(message.Sender, AllowedTestRecipient))
                {
                    AddAudit(
                        mailbox,
                        message,
                        EmailAutomationActions.SkippedAutomationGeneratedMessage,
                        "Sender is the ODMON automation mailbox.",
                        resolution: resolution);
                    continue;
                }

                if (EmailEquals(resolvedTargetEmail, mailbox))
                {
                    AddAudit(
                        mailbox,
                        message,
                        EmailAutomationActions.SkippedTargetIsMailboxOwner,
                        resolution: resolution);
                    _logger.LogInformation(
                        "EMAILAUTOMATION real forwarding skipped because resolved target is the monitored mailbox owner. Mailbox={Mailbox}, RuleName={RuleName}, ResolvedTargetEmail={ResolvedTargetEmail}",
                        mailbox,
                        rule.Name,
                        resolvedTargetEmail);
                    continue;
                }

                if (IsRecipient(message.ToRecipients, resolvedTargetEmail) ||
                    IsRecipient(message.CcRecipients, resolvedTargetEmail))
                {
                    AddAudit(
                        mailbox,
                        message,
                        EmailAutomationActions.SkippedTargetAlreadyRecipient,
                        "Resolved target is already an original To or Cc recipient.",
                        resolution: resolution);
                    continue;
                }

                if (IsForwardOrReplyThread(message.Subject))
                {
                    AddAudit(
                        mailbox,
                        message,
                        EmailAutomationActions.SkippedForwardOrReplyThread,
                        "Subject indicates an already-forwarded or replied thread.",
                        resolution: resolution);
                    continue;
                }

                if (EmailEquals(message.Sender, resolvedTargetEmail))
                {
                    AddAudit(
                        mailbox,
                        message,
                        EmailAutomationActions.SkippedSenderIsResolvedTarget,
                        "Sender is the resolved target employee.",
                        resolution: resolution);
                    continue;
                }

                forwarded += await ForwardAsync(
                    mailbox,
                    rule.Name,
                    message,
                    resolvedTargetEmail,
                    EmailAutomationActions.ForwardedToResolvedMailbox,
                    resolution,
                    forwardsThisCycle + forwarded,
                    "resolved",
                    cancellationToken);
            }

            await _db.SaveChangesAsync(cancellationToken);
            return forwarded;
        }

        private async Task<int> ForwardAsync(
            string mailbox,
            string ruleName,
            EmailAutomationMessage message,
            string targetEmail,
            string successAction,
            RoutingResolution resolution,
            int forwardsThisCycle,
            string forwardMode,
            CancellationToken cancellationToken)
        {
            if (forwardsThisCycle >= Math.Max(0, _settings.MaxForwardsPerCycle))
            {
                throw new ForwardLimitReachedException();
            }

            var idempotencyKey = CreateIdempotencyKey(
                GetStableMessageIdentifier(message),
                ruleName,
                targetEmail);
            if (await _db.EmailAutomationLogs.AnyAsync(
                x => x.IdempotencyKey == idempotencyKey,
                cancellationToken))
            {
                AddAudit(
                    mailbox,
                    message,
                    EmailAutomationActions.SkippedAlreadyProcessed,
                    resolution: resolution);
                return 0;
            }

            var forwardAudit = AddAudit(
                mailbox,
                message,
                successAction,
                idempotencyKey: idempotencyKey,
                resolution: resolution);

            // Reserve the unique key before the external side effect. This deliberately
            // provides at-most-once forwarding if Graph's response is ambiguous.
            await _db.SaveChangesAsync(cancellationToken);
            try
            {
                _logger.LogInformation(
                    "EMAILAUTOMATION forwarding message. ForwardMode={ForwardMode}, Mailbox={Mailbox}, ResolvedTikCounter={ResolvedTikCounter}, ResolvedTargetEmail={ResolvedTargetEmail}",
                    forwardMode,
                    mailbox,
                    resolution.CaseMatch.TikCounter,
                    resolution.ResolvedTargetEmail);
                await _graphClient.ForwardMessageAsync(
                    mailbox,
                    message.Id,
                    targetEmail,
                    string.Equals(forwardMode, "test", StringComparison.OrdinalIgnoreCase)
                        ? "ODMON email automation test forward"
                        : null,
                    cancellationToken);
                forwardAudit.ProcessedAtUtc = UtcNow();
                return 1;
            }
            catch (GraphThrottledException)
            {
                forwardAudit.Action = EmailAutomationActions.Failed;
                forwardAudit.ErrorMessage = "GraphThrottled";
                await _db.SaveChangesAsync(cancellationToken);
                throw;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                forwardAudit.Action = EmailAutomationActions.Failed;
                forwardAudit.ErrorMessage = "ForwardFailed";
                _logger.LogError(
                    "EMAILAUTOMATION {ForwardMode} forward failed. Mailbox={Mailbox}, TikCounter={TikCounter}, ErrorCategory={ErrorCategory}",
                    forwardMode,
                    mailbox,
                    resolution.CaseMatch.TikCounter,
                    ex.GetType().Name);
                return 0;
            }
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

        internal static bool IsForwardOrReplyThread(string? subject)
            => !string.IsNullOrWhiteSpace(subject) &&
               ForwardOrReplyPrefixRegex.IsMatch(subject);

        private static bool IsRecipient(
            IReadOnlyList<string> recipients,
            string targetEmail)
            => recipients.Any(x => EmailEquals(x, targetEmail));

        private static bool EmailEquals(string? left, string? right)
            => !string.IsNullOrWhiteSpace(left) &&
               !string.IsNullOrWhiteSpace(right) &&
               string.Equals(
                   left.Trim(),
                   right.Trim(),
                   StringComparison.OrdinalIgnoreCase);

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

        internal string CreateIdempotencyKey(
            string messageIdentifier,
            string ruleName,
            string targetEmail)
        {
            var value = string.Join(
                "\n",
                "forward-v1",
                messageIdentifier.Trim(),
                ruleName.Trim().ToLowerInvariant(),
                targetEmail.Trim().ToLowerInvariant());
            return CreateFingerprint(value);
        }

        private string CreateMessageFingerprint(
            string mailbox,
            EmailAutomationMessage message)
            => CreateFingerprint(string.Join(
                "\n",
                "message-v1",
                mailbox.Trim().ToLowerInvariant(),
                GetStableMessageIdentifier(message)));

        private static string GetStableMessageIdentifier(EmailAutomationMessage message)
            => !string.IsNullOrWhiteSpace(message.InternetMessageId)
                ? $"internet:{message.InternetMessageId.Trim()}"
                : $"graph:{message.Id.Trim()}";

        private string CreateFingerprint(string value)
        {
            using var hmac = new HMACSHA256(
                Encoding.UTF8.GetBytes(_settings.FingerprintKey));
            return Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(value)));
        }

        private EmailAutomationLog AddAudit(
            string mailbox,
            EmailAutomationMessage message,
            string action,
            string? errorCategory = null,
            string? idempotencyKey = null,
            RoutingResolution? resolution = null)
        {
            var audit = new EmailAutomationLog
            {
                Mailbox = mailbox,
                RuleName = null,
                InternetMessageId = null,
                GraphMessageId = CreateMessageFingerprint(mailbox, message),
                Subject = null,
                Sender = null,
                ReceivedDateTimeUtc = null,
                DetectedCourtCaseNumber = null,
                ResolvedTikCounter = resolution?.CaseMatch.TikCounter,
                ResolvedTikNumber = null,
                ResolvedClientNumber = null,
                ResolvedTargetEmail = resolution?.ResolvedTargetEmail,
                ActualForwardTo = null,
                TargetEmail = null,
                Action = action,
                IdempotencyKey = idempotencyKey,
                ErrorMessage = SanitizeErrorCategory(errorCategory),
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

        private static string? SanitizeErrorCategory(string? value)
            => value switch
            {
                null => null,
                "MissingRouting" => value,
                "GraphThrottled" => value,
                "ForwardFailed" => value,
                "Test forwarding target is not on the phase-one allowlist." =>
                    "TestTargetNotAllowed",
                "Real forwarding was skipped because test forwarding is enabled." =>
                    "TestModeEnabled",
                "Sender is the ODMON automation mailbox." => "AutomationSender",
                "Resolved target is already an original To or Cc recipient." =>
                    "TargetAlreadyRecipient",
                "Subject indicates an already-forwarded or replied thread." =>
                    "ForwardOrReplyThread",
                "Sender is the resolved target employee." => "SenderIsTarget",
                _ => "OperationalFailure"
            };

        private sealed record RoutingResolution(
            EmailAutomationCaseMatch CaseMatch,
            int? ClientNumber,
            string? ResolvedTargetEmail);

        private sealed class ForwardLimitReachedException : Exception
        {
        }
    }
}
