using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Odmon.Worker.Configuration;
using Odmon.Worker.Data;
using Odmon.Worker.Models;
using Odmon.Worker.OdcanitAccess;
using Odmon.Worker.Services;
using Xunit;

namespace Odmon.Worker.Tests
{
    public sealed class EmailAutomationTests
    {
        private static readonly DateTime BaselineUtc =
            new(2026, 6, 9, 8, 0, 0, DateTimeKind.Utc);

        [Theory]
        [InlineData("Update 123-45-67 received", "123-45-67")]
        [InlineData("Case 1-22-33", "1-22-33")]
        [InlineData("Case 1234567-22-33", "")]
        [InlineData("No proceeding number", "")]
        public void SubjectRegexDetection(string subject, string expected)
        {
            var match = EmailAutomationService.MatchSubject(
                @"\b\d{1,6}-\d{2}-\d{2}\b",
                subject);

            Assert.Equal(expected, match.Success ? match.Value : string.Empty);
        }

        [Theory]
        [InlineData(5, "amir@ezer-law.com")]
        [InlineData(8, "amir@ezer-law.com")]
        [InlineData(23, "amir@ezer-law.com")]
        [InlineData(253, "amir@ezer-law.com")]
        [InlineData(101, "amir@ezer-law.com")]
        [InlineData(3, "amir@ezer-law.com")]
        [InlineData(2, "yonatan@ezer-law.com")]
        [InlineData(15, "yonatan@ezer-law.com")]
        [InlineData(999, "eden@ezer-law.com")]
        public void ClientRoutingResolvesExpectedEmployee(
            int clientNumber,
            string expectedEmail)
        {
            Assert.Equal(
                expectedEmail,
                EmailAutomationService.ResolveTargetEmail(clientNumber));
        }

        [Fact]
        public async Task DryRunNeverForwards()
        {
            await using var db = CreateDb();
            var graph = new FakeGraphClient(Message("graph-1", "<mail-1@test>"));
            var service = CreateService(db, graph, dryRun: true, testForwardEnabled: true);

            await service.RunAsync(CancellationToken.None);

            Assert.Empty(graph.Forwards);
            Assert.Contains(
                db.EmailAutomationLogs,
                x => x.Action == EmailAutomationActions.DryRunWouldForward);
            Assert.All(db.EmailAutomationLogs, AssertPrivacyMinimizedAudit);
        }

        [Fact]
        public async Task TestModeForwardsOnlyToOdmon()
        {
            await using var db = CreateDb();
            var graph = new FakeGraphClient(
                Message("graph-1", "<mail-1@test>"),
                Message("graph-2", "<mail-2@test>"));
            var settings = CreateSettings(dryRun: false, testForwardEnabled: true);
            settings.Mailboxes[0].Rules.Add(new EmailAutomationRuleSettings
            {
                Name = "UnsafeTarget",
                Enabled = true,
                SubjectRegex = @"\b\d{1,6}-\d{2}-\d{2}\b",
                TestForwardEnabled = true,
                TestForwardTo = "employee@ezer-law.com"
            });
            var service = CreateService(db, graph, settings);

            await service.RunAsync(CancellationToken.None);

            Assert.NotEmpty(graph.Forwards);
            Assert.All(
                graph.Forwards,
                x => Assert.Equal(
                    EmailAutomationService.AllowedTestRecipient,
                    x.Target,
                    ignoreCase: true));
            Assert.Contains(
                db.EmailAutomationLogs,
                x => x.Action == EmailAutomationActions.Failed &&
                     x.ErrorMessage == "TestTargetNotAllowed");
            Assert.All(db.EmailAutomationLogs, AssertPrivacyMinimizedAudit);
        }

        [Fact]
        public async Task IdempotencyPreventsDuplicateForward()
        {
            await using var db = CreateDb();
            var graph = new FakeGraphClient(Message("graph-1", "<same@test>"));
            var service = CreateService(db, graph, dryRun: false, testForwardEnabled: true);
            await service.RunAsync(CancellationToken.None);
            var auditCount = db.EmailAutomationLogs.Count();

            graph.Enqueue(Message("graph-2", "<same@test>"), "delta-2");
            await service.RunAsync(CancellationToken.None);

            Assert.Single(graph.Forwards);
            Assert.Equal(auditCount, db.EmailAutomationLogs.Count());
            Assert.DoesNotContain(db.EmailAutomationLogs, x => x.GraphMessageId.Contains("same@test"));
        }

        [Fact]
        public async Task AlreadyProcessedGraphMessageIsSkipped()
        {
            await using var db = CreateDb();
            var graph = new FakeGraphClient(Message("graph-1", "<mail-1@test>"));
            var service = CreateService(db, graph, dryRun: false, testForwardEnabled: true);
            await service.RunAsync(CancellationToken.None);
            var auditCount = db.EmailAutomationLogs.Count();

            graph.Enqueue(Message("graph-1", "<mail-1@test>"), "delta-2");
            await service.RunAsync(CancellationToken.None);

            Assert.Single(graph.Forwards);
            Assert.Equal(auditCount, db.EmailAutomationLogs.Count());
        }

        [Fact]
        public async Task CompletedDeltaStateIsSaved()
        {
            await using var db = CreateDb();
            var graph = new FakeGraphClient(Message("graph-1", "<mail-1@test>"));
            var service = CreateService(db, graph, dryRun: true, testForwardEnabled: false);

            await service.RunAsync(CancellationToken.None);

            var state = Assert.Single(db.EmailAutomationMailboxStates);
            Assert.Equal("delta-1", state.DeltaLink);
            Assert.NotNull(state.LastSuccessfulSyncUtc);
            Assert.Equal(BaselineUtc, state.ProcessingFromUtc);
        }

        [Fact]
        public async Task MessageLimitDoesNotAdvanceDeltaPastDeferredMessages()
        {
            await using var db = CreateDb();
            var graph = new FakeGraphClient(
                Message("graph-1", "<mail-1@test>"),
                Message("graph-2", "<mail-2@test>"));
            var settings = CreateSettings(dryRun: true, testForwardEnabled: false);
            settings.MaxMessagesPerCycle = 1;
            var service = CreateService(db, graph, settings);

            await service.RunAsync(CancellationToken.None);

            var state = Assert.Single(db.EmailAutomationMailboxStates);
            Assert.Null(state.DeltaLink);
            Assert.Null(state.LastSuccessfulSyncUtc);
            Assert.Single(
                db.EmailAutomationLogs.Where(x => x.Action == EmailAutomationActions.Matched));
        }

        [Fact]
        public async Task GraphThrottleDuringForwardDoesNotAdvanceDelta()
        {
            await using var db = CreateDb();
            var graph = new FakeGraphClient(Message("graph-1", "<mail-1@test>"))
            {
                ThrottleForward = true
            };
            var service = CreateService(db, graph, dryRun: false, testForwardEnabled: true);

            await Assert.ThrowsAsync<GraphThrottledException>(
                () => service.RunAsync(CancellationToken.None));

            var state = Assert.Single(db.EmailAutomationMailboxStates);
            Assert.Null(state.DeltaLink);
            Assert.Contains(
                db.EmailAutomationLogs,
                x => x.Action == EmailAutomationActions.Failed &&
                     x.IdempotencyKey != null);
        }

        [Fact]
        public async Task FirstRunDoesNotProcessHistoricalMessages()
        {
            await using var db = CreateDb();
            var oldMessage = Message("old-graph", "<old@test>") with
            {
                ReceivedDateTimeUtc = BaselineUtc.AddSeconds(-1)
            };
            var graph = new FakeGraphClient(oldMessage);
            var service = CreateService(db, graph, dryRun: false, testForwardEnabled: true);

            await service.RunAsync(CancellationToken.None);

            Assert.Empty(graph.Forwards);
            Assert.Empty(db.EmailAutomationLogs);
            Assert.Equal(
                "delta-1",
                Assert.Single(db.EmailAutomationMailboxStates).DeltaLink);
        }

        [Fact]
        public async Task Client5_ResolvesToAmir_ButActuallyForwardsOnlyToOdmon()
        {
            await using var db = CreateDb();
            var graph = new FakeGraphClient(Message("graph-1", "<mail-1@test>"));
            var resolver = new FakeCaseResolver(CaseMatch("5\\456"));
            var service = CreateService(
                db,
                graph,
                CreateSettings(dryRun: false, testForwardEnabled: true),
                resolver);

            await service.RunAsync(CancellationToken.None);

            var audit = Assert.Single(
                db.EmailAutomationLogs.Where(
                    x => x.Action == EmailAutomationActions.ForwardedToTestMailbox));
            Assert.Equal("amir@ezer-law.com", audit.ResolvedTargetEmail);
            Assert.Equal(456, audit.ResolvedTikCounter);
            AssertPrivacyMinimizedAudit(audit);
            Assert.Equal("odmon@ezer-law.com", Assert.Single(graph.Forwards).Target);
            Assert.Equal("ODMON email automation test forward", Assert.Single(graph.ForwardComments));
        }

        [Theory]
        [InlineData("2\\123")]
        [InlineData("15\\123")]
        public async Task Client2Or15_ResolvesToYonatan(string clientVisualId)
        {
            await using var db = CreateDb();
            var graph = new FakeGraphClient(Message("graph-1", "<mail-1@test>"));
            var service = CreateService(
                db,
                graph,
                CreateSettings(dryRun: true, testForwardEnabled: true),
                new FakeCaseResolver(CaseMatch(clientVisualId)));

            await service.RunAsync(CancellationToken.None);

            Assert.Contains(
                db.EmailAutomationLogs,
                x => x.Action == EmailAutomationActions.DryRunWouldForward &&
                     x.ResolvedTargetEmail == "yonatan@ezer-law.com");
            Assert.Empty(graph.Forwards);
        }

        [Fact]
        public async Task OtherClient_ResolvesToEden()
        {
            await using var db = CreateDb();
            var graph = new FakeGraphClient(Message("graph-1", "<mail-1@test>"));
            var service = CreateService(
                db,
                graph,
                CreateSettings(dryRun: true, testForwardEnabled: true),
                new FakeCaseResolver(CaseMatch("999\\1")));

            await service.RunAsync(CancellationToken.None);

            Assert.Contains(
                db.EmailAutomationLogs,
                x => x.Action == EmailAutomationActions.DryRunWouldForward &&
                     x.ResolvedTargetEmail == "eden@ezer-law.com");
            Assert.Empty(graph.Forwards);
        }

        [Fact]
        public async Task NoMatchingCase_DoesNotForward_AndCreatesNoAudit()
        {
            await using var db = CreateDb();
            var graph = new FakeGraphClient(Message("graph-1", "<mail-1@test>"));
            var service = CreateService(
                db,
                graph,
                CreateSettings(dryRun: false, testForwardEnabled: true),
                new FakeCaseResolver(null));

            await service.RunAsync(CancellationToken.None);

            Assert.Empty(graph.Forwards);
            Assert.Empty(db.EmailAutomationLogs);
        }

        [Fact]
        public async Task UnmatchedMessageDoesNotPersistOrLogIdentifiers()
        {
            const string graphId = "sensitive-graph-id";
            const string internetId = "<sensitive-internet-id@example.test>";
            const string subject = "Private court update 987-65-43";
            const string sender = "private.sender@example.test";
            var message = Message(graphId, internetId) with
            {
                Subject = subject,
                Sender = sender
            };
            await using var db = CreateDb();
            var graph = new FakeGraphClient(message);
            var logger = new CollectingLogger<EmailAutomationService>();
            var service = CreateService(
                db,
                graph,
                CreateSettings(dryRun: false, testForwardEnabled: true),
                new FakeCaseResolver(null),
                logger);

            await service.RunAsync(CancellationToken.None);

            Assert.Empty(db.EmailAutomationLogs);
            var logText = string.Join("\n", logger.Messages);
            Assert.DoesNotContain(graphId, logText, StringComparison.Ordinal);
            Assert.DoesNotContain(internetId, logText, StringComparison.Ordinal);
            Assert.DoesNotContain(subject, logText, StringComparison.Ordinal);
            Assert.DoesNotContain(sender, logText, StringComparison.Ordinal);
            Assert.DoesNotContain("987-65-43", logText, StringComparison.Ordinal);
        }

        [Fact]
        public async Task AmbiguousCaseDoesNotForwardOrCreateAudit()
        {
            await using var db = CreateDb();
            var graph = new FakeGraphClient(Message("ambiguous-graph", "<ambiguous@test>"));
            var ambiguous = new EmailAutomationCaseMatch(0, null, null, IsAmbiguous: true);

            await CreateService(
                db,
                graph,
                CreateSettings(dryRun: false, testForwardEnabled: true),
                new FakeCaseResolver(ambiguous))
                .RunAsync(CancellationToken.None);

            Assert.Empty(graph.Forwards);
            Assert.Empty(db.EmailAutomationLogs);
        }

        [Fact]
        public async Task ForwardFailureStoresAndLogsOnlySanitizedCategory()
        {
            const string sensitiveError =
                "subject=Private 123-45-67 sender=secret@example.test graph=sensitive-id";
            await using var db = CreateDb();
            var graph = new FakeGraphClient(Message("sensitive-id", "<mail@test>"))
            {
                ForwardException = new InvalidOperationException(sensitiveError)
            };
            var logger = new CollectingLogger<EmailAutomationService>();
            var service = CreateService(
                db,
                graph,
                CreateSettings(dryRun: false, testForwardEnabled: true),
                new FakeCaseResolver(CaseMatch("5\\456")),
                logger);

            await service.RunAsync(CancellationToken.None);

            var failed = Assert.Single(
                db.EmailAutomationLogs.Where(x => x.Action == EmailAutomationActions.Failed));
            Assert.Equal("ForwardFailed", failed.ErrorMessage);
            AssertPrivacyMinimizedAudit(failed);
            Assert.DoesNotContain(
                sensitiveError,
                string.Join("\n", logger.Messages),
                StringComparison.Ordinal);
        }

        [Fact]
        public async Task OriginalGraphMessageIsForwardedWithoutContentOrAttachmentPersistence()
        {
            await using var db = CreateDb();
            var graph = new FakeGraphClient(Message("graph-with-attachments", "<attachment-mail@test>"));
            var service = CreateService(db, graph, dryRun: false, testForwardEnabled: true);

            await service.RunAsync(CancellationToken.None);

            var forward = Assert.Single(graph.Forwards);
            Assert.Equal("graph-with-attachments", forward.MessageId);
            Assert.All(db.EmailAutomationLogs, AssertPrivacyMinimizedAudit);
            var auditProperties = typeof(EmailAutomationLog)
                .GetProperties()
                .Select(x => x.Name)
                .ToArray();
            Assert.DoesNotContain(auditProperties, x => x.Contains("Body", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(auditProperties, x => x.Contains("Attachment", StringComparison.OrdinalIgnoreCase));
        }

        [Fact]
        public async Task MissingClientNumber_DoesNotForward_AndAuditsNoClientMatch()
        {
            await using var db = CreateDb();
            var graph = new FakeGraphClient(Message("graph-1", "<mail-1@test>"));
            var service = CreateService(
                db,
                graph,
                CreateSettings(dryRun: false, testForwardEnabled: true),
                new FakeCaseResolver(CaseMatch(null)));

            await service.RunAsync(CancellationToken.None);

            Assert.Empty(graph.Forwards);
            Assert.Contains(
                db.EmailAutomationLogs,
                x => x.Action == EmailAutomationActions.NoClientMatch &&
                     x.ResolvedTikCounter == 456 &&
                     x.ErrorMessage == "MissingRouting");
            Assert.All(db.EmailAutomationLogs, AssertPrivacyMinimizedAudit);
        }

        [Fact]
        public async Task RealForwardDisabled_DoesNotForward()
        {
            await using var db = CreateDb();
            var graph = new FakeGraphClient(RealForwardMessage());
            var settings = CreateSettings(
                dryRun: false,
                testForwardEnabled: false,
                realForwardEnabled: false);

            await CreateService(db, graph, settings).RunAsync(CancellationToken.None);

            Assert.Empty(graph.Forwards);
            Assert.DoesNotContain(
                db.EmailAutomationLogs,
                x => x.Action == EmailAutomationActions.ForwardedToResolvedMailbox);
        }

        [Fact]
        public async Task DryRunWithRealForwardEnabled_DoesNotForward()
        {
            await using var db = CreateDb();
            var graph = new FakeGraphClient(RealForwardMessage());
            var settings = CreateSettings(
                dryRun: true,
                testForwardEnabled: false,
                realForwardEnabled: true);

            await CreateService(db, graph, settings).RunAsync(CancellationToken.None);

            Assert.Empty(graph.Forwards);
            Assert.Contains(
                db.EmailAutomationLogs,
                x => x.Action == EmailAutomationActions.DryRunWouldForward &&
                     x.ResolvedTargetEmail == "amir@ezer-law.com");
        }

        [Fact]
        public async Task TestAndRealEnabled_ForwardsOnlyToTestMailbox()
        {
            await using var db = CreateDb();
            var graph = new FakeGraphClient(RealForwardMessage());
            var settings = CreateSettings(
                dryRun: false,
                testForwardEnabled: true,
                realForwardEnabled: true);

            await CreateService(db, graph, settings).RunAsync(CancellationToken.None);

            Assert.Equal("odmon@ezer-law.com", Assert.Single(graph.Forwards).Target);
            Assert.Contains(
                db.EmailAutomationLogs,
                x => x.Action == EmailAutomationActions.SkippedRealForwardBecauseTestModeEnabled &&
                     x.ResolvedTargetEmail == "amir@ezer-law.com" &&
                     x.ActualForwardTo == null);
            Assert.DoesNotContain(
                db.EmailAutomationLogs,
                x => x.Action == EmailAutomationActions.ForwardedToResolvedMailbox);
        }

        [Fact]
        public async Task TestForwardMode_IgnoresRealForwardLoopChecks()
        {
            await using var db = CreateDb();
            var graph = new FakeGraphClient(
                RealForwardMessage(
                    subject: "RE: Court update 123-45-67",
                    sender: "amir@ezer-law.com",
                    toRecipients: ["amir@ezer-law.com"],
                    ccRecipients: ["amir@ezer-law.com"]));
            var settings = CreateSettings(
                dryRun: false,
                testForwardEnabled: true,
                realForwardEnabled: true);

            await CreateService(db, graph, settings).RunAsync(CancellationToken.None);

            Assert.Equal("odmon@ezer-law.com", Assert.Single(graph.Forwards).Target);
            Assert.Contains(
                db.EmailAutomationLogs,
                x => x.Action == EmailAutomationActions.ForwardedToTestMailbox);
            Assert.DoesNotContain(
                db.EmailAutomationLogs,
                x => x.Action == EmailAutomationActions.SkippedTargetAlreadyRecipient ||
                     x.Action == EmailAutomationActions.SkippedForwardOrReplyThread ||
                     x.Action == EmailAutomationActions.SkippedSenderIsResolvedTarget);
        }

        [Theory]
        [InlineData("2\\123", "yonatan@ezer-law.com")]
        [InlineData("999\\1", "eden@ezer-law.com")]
        public async Task NonOwnerTarget_RealForwardEnabled_ForwardsToResolvedMailbox(
            string clientVisualId,
            string expectedTarget)
        {
            await using var db = CreateDb();
            var graph = new FakeGraphClient(RealForwardMessage());
            var settings = CreateSettings(
                dryRun: false,
                testForwardEnabled: false,
                realForwardEnabled: true);

            await CreateService(
                db,
                graph,
                settings,
                new FakeCaseResolver(CaseMatch(clientVisualId)))
                .RunAsync(CancellationToken.None);

            Assert.Equal(expectedTarget, Assert.Single(graph.Forwards).Target);
            Assert.True(string.IsNullOrEmpty(Assert.Single(graph.ForwardComments)));
            var audit = Assert.Single(
                db.EmailAutomationLogs.Where(
                    x => x.Action == EmailAutomationActions.ForwardedToResolvedMailbox));
            Assert.Equal(expectedTarget, audit.ResolvedTargetEmail);
            AssertPrivacyMinimizedAudit(audit);
        }

        [Fact]
        public async Task MailboxOwnerTarget_SkipsWithoutError()
        {
            await using var db = CreateDb();
            var graph = new FakeGraphClient(RealForwardMessage());
            var settings = CreateSettings(
                dryRun: false,
                testForwardEnabled: false,
                realForwardEnabled: true);

            await CreateService(db, graph, settings).RunAsync(CancellationToken.None);

            Assert.Empty(graph.Forwards);
            var audit = Assert.Single(
                db.EmailAutomationLogs.Where(
                    x => x.Action == EmailAutomationActions.SkippedTargetIsMailboxOwner));
            Assert.Equal("amir@ezer-law.com", audit.ResolvedTargetEmail);
            Assert.Null(audit.ActualForwardTo);
            Assert.Null(audit.ErrorMessage);
        }

        [Theory]
        [InlineData("2\\123", "yonatan@ezer-law.com")]
        [InlineData("999\\1", "eden@ezer-law.com")]
        public async Task NonOwnerTargetAlreadyInTo_SkipsRealForward(
            string clientVisualId,
            string targetEmail)
        {
            await AssertRealForwardSkipAsync(
                RealForwardMessage(toRecipients: [targetEmail]),
                EmailAutomationActions.SkippedTargetAlreadyRecipient,
                clientVisualId,
                targetEmail);
        }

        [Theory]
        [InlineData("2\\123", "yonatan@ezer-law.com")]
        [InlineData("999\\1", "eden@ezer-law.com")]
        public async Task NonOwnerTargetAlreadyInCc_SkipsRealForward(
            string clientVisualId,
            string targetEmail)
        {
            await AssertRealForwardSkipAsync(
                RealForwardMessage(ccRecipients: [targetEmail]),
                EmailAutomationActions.SkippedTargetAlreadyRecipient,
                clientVisualId,
                targetEmail);
        }

        [Theory]
        [InlineData("RE: Court update 123-45-67")]
        [InlineData("FW: Court update 123-45-67")]
        [InlineData("FWD: Court update 123-45-67")]
        [InlineData("השב: Court update 123-45-67")]
        [InlineData("הועבר: Court update 123-45-67")]
        [InlineData("Case update FW: 123-45-67")]
        public async Task ForwardOrReplySubject_SkipsRealForward(string subject)
        {
            await AssertRealForwardSkipAsync(
                RealForwardMessage(subject: subject),
                EmailAutomationActions.SkippedForwardOrReplyThread,
                "2\\123",
                "yonatan@ezer-law.com");
        }

        [Fact]
        public async Task SenderIsResolvedTarget_SkipsRealForward()
        {
            await AssertRealForwardSkipAsync(
                RealForwardMessage(sender: "yonatan@ezer-law.com"),
                EmailAutomationActions.SkippedSenderIsResolvedTarget,
                "2\\123",
                "yonatan@ezer-law.com");
        }

        [Fact]
        public async Task AutomationSender_SkipsRealForward()
        {
            await AssertRealForwardSkipAsync(
                RealForwardMessage(sender: "odmon@ezer-law.com"),
                EmailAutomationActions.SkippedAutomationGeneratedMessage,
                "2\\123",
                "yonatan@ezer-law.com");
        }

        [Fact]
        public async Task RealForwardIdempotencyPreventsDuplicateForward()
        {
            await using var db = CreateDb();
            var graph = new FakeGraphClient(
                RealForwardMessage("graph-1", "<same-real@test>"));
            var settings = CreateSettings(
                dryRun: false,
                testForwardEnabled: false,
                realForwardEnabled: true);
            var service = CreateService(
                db,
                graph,
                settings,
                new FakeCaseResolver(CaseMatch("2\\123")));
            await service.RunAsync(CancellationToken.None);
            var auditCount = db.EmailAutomationLogs.Count();

            graph.Enqueue(
                RealForwardMessage("graph-2", "<same-real@test>"),
                "delta-2");
            await service.RunAsync(CancellationToken.None);

            Assert.Single(graph.Forwards);
            Assert.Equal(auditCount, db.EmailAutomationLogs.Count());
        }

        [Fact]
        public async Task RealForwardLimit_DoesNotAdvanceDeltaPastEligibleMessage()
        {
            await using var db = CreateDb();
            var graph = new FakeGraphClient(RealForwardMessage());
            var settings = CreateSettings(
                dryRun: false,
                testForwardEnabled: false,
                realForwardEnabled: true);
            settings.MaxForwardsPerCycle = 0;

            await CreateService(
                db,
                graph,
                settings,
                new FakeCaseResolver(CaseMatch("2\\123")))
                .RunAsync(CancellationToken.None);

            Assert.Empty(graph.Forwards);
            var state = Assert.Single(db.EmailAutomationMailboxStates);
            Assert.Null(state.DeltaLink);
            Assert.Null(state.LastSuccessfulSyncUtc);
        }

        private static async Task AssertRealForwardSkipAsync(
            EmailAutomationMessage message,
            string expectedAction,
            string clientVisualId,
            string expectedTarget)
        {
            await using var db = CreateDb();
            var graph = new FakeGraphClient(message);
            var settings = CreateSettings(
                dryRun: false,
                testForwardEnabled: false,
                realForwardEnabled: true);

            await CreateService(
                db,
                graph,
                settings,
                new FakeCaseResolver(CaseMatch(clientVisualId)))
                .RunAsync(CancellationToken.None);

            Assert.Empty(graph.Forwards);
            var audit = Assert.Single(
                db.EmailAutomationLogs.Where(x => x.Action == expectedAction));
            Assert.Equal(expectedTarget, audit.ResolvedTargetEmail);
            Assert.Null(audit.ActualForwardTo);
            AssertPrivacyMinimizedAudit(audit);
        }

        private static void AssertPrivacyMinimizedAudit(EmailAutomationLog audit)
        {
            Assert.Null(audit.RuleName);
            Assert.Null(audit.InternetMessageId);
            Assert.Matches("^[0-9A-F]{64}$", audit.GraphMessageId);
            Assert.Null(audit.Subject);
            Assert.Null(audit.Sender);
            Assert.Null(audit.ReceivedDateTimeUtc);
            Assert.Null(audit.DetectedCourtCaseNumber);
            Assert.Null(audit.ResolvedTikNumber);
            Assert.Null(audit.ResolvedClientNumber);
            Assert.Null(audit.ActualForwardTo);
            Assert.Null(audit.TargetEmail);
        }

        private static EmailAutomationCaseMatch CaseMatch(string? clientVisualId)
            => new(456, "5/2000", clientVisualId);

        private static EmailAutomationMessage Message(string graphId, string internetMessageId)
            => new(
                graphId,
                internetMessageId,
                "Court update 123-45-67",
                "court@example.test",
                ["amir@ezer-law.com"],
                [],
                BaselineUtc.AddMinutes(1));

        private static EmailAutomationMessage RealForwardMessage(
            string graphId = "graph-real",
            string internetMessageId = "<real@test>",
            string subject = "Court update 123-45-67",
            string sender = "court@example.test",
            IReadOnlyList<string>? toRecipients = null,
            IReadOnlyList<string>? ccRecipients = null)
            => new(
                graphId,
                internetMessageId,
                subject,
                sender,
                toRecipients ?? ["intake@example.test"],
                ccRecipients ?? [],
                BaselineUtc.AddMinutes(1));

        private static IntegrationDbContext CreateDb()
        {
            var options = new DbContextOptionsBuilder<IntegrationDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options;
            return new IntegrationDbContext(options);
        }

        private static EmailAutomationService CreateService(
            IntegrationDbContext db,
            FakeGraphClient graph,
            bool dryRun,
            bool testForwardEnabled)
            => CreateService(
                db,
                graph,
                CreateSettings(dryRun, testForwardEnabled));

        private static EmailAutomationService CreateService(
            IntegrationDbContext db,
            FakeGraphClient graph,
            EmailAutomationSettings settings)
            => CreateService(
                db,
                graph,
                settings,
                new FakeCaseResolver(CaseMatch("5\\456")));

        private static EmailAutomationService CreateService(
            IntegrationDbContext db,
            FakeGraphClient graph,
            EmailAutomationSettings settings,
            IEmailAutomationCaseResolver resolver)
            => CreateService(
                db,
                graph,
                settings,
                resolver,
                NullLogger<EmailAutomationService>.Instance);

        private static EmailAutomationService CreateService(
            IntegrationDbContext db,
            FakeGraphClient graph,
            EmailAutomationSettings settings,
            IEmailAutomationCaseResolver resolver,
            ILogger<EmailAutomationService> logger)
            => new(
                db,
                graph,
                resolver,
                Options.Create(settings),
                logger,
                new FixedTimeProvider(BaselineUtc));

        private static EmailAutomationSettings CreateSettings(
            bool dryRun,
            bool testForwardEnabled,
            bool realForwardEnabled = false)
            => new()
            {
                Enabled = true,
                DryRun = dryRun,
                RealForwardEnabled = realForwardEnabled,
                MaxMessagesPerCycle = 50,
                MaxForwardsPerCycle = 20,
                FingerprintKey = "unit-test-email-automation-fingerprint-key",
                StartProcessingFromUtc = BaselineUtc,
                Mailboxes =
                [
                    new EmailAutomationMailboxSettings
                    {
                        Address = "amir@ezer-law.com",
                        InboxFolder = "Inbox",
                        Rules =
                        [
                            new EmailAutomationRuleSettings
                            {
                                Name = "CourtCaseRoutingTest",
                                Enabled = true,
                                SubjectRegex = @"\b\d{1,6}-\d{2}-\d{2}\b",
                                TestForwardEnabled = testForwardEnabled,
                                TestForwardTo = "odmon@ezer-law.com"
                            }
                        ]
                    }
                ]
            };

        private sealed class FakeGraphClient : IEmailAutomationGraphClient
        {
            private readonly Queue<EmailAutomationDeltaPage> _pages = new();

            public FakeGraphClient(params EmailAutomationMessage[] messages)
            {
                Enqueue(messages, "delta-1");
            }

            public List<(string Mailbox, string MessageId, string Target)> Forwards { get; } = [];
            public bool ThrottleForward { get; init; }
            public Exception? ForwardException { get; init; }

            public void Enqueue(EmailAutomationMessage message, string deltaLink)
                => Enqueue([message], deltaLink);

            private void Enqueue(IReadOnlyList<EmailAutomationMessage> messages, string deltaLink)
                => _pages.Enqueue(new EmailAutomationDeltaPage(messages, null, deltaLink));

            public Task<EmailAutomationDeltaPage> GetDeltaPageAsync(
                string mailbox,
                string folderId,
                string? deltaLink,
                DateTime processingFromUtc,
                int pageSize,
                CancellationToken cancellationToken)
                => Task.FromResult(_pages.Dequeue());

            public Task ForwardMessageAsync(
                string mailbox,
                string graphMessageId,
                string targetEmail,
                string? comment,
                CancellationToken cancellationToken)
            {
                if (ThrottleForward)
                {
                    throw new GraphThrottledException(TimeSpan.FromMinutes(1));
                }

                if (ForwardException != null)
                {
                    throw ForwardException;
                }

                Forwards.Add((mailbox, graphMessageId, targetEmail));
                ForwardComments.Add(comment);
                return Task.CompletedTask;
            }

            public List<string?> ForwardComments { get; } = new();
        }

        private sealed class FixedTimeProvider(DateTime utcNow) : TimeProvider
        {
            public override DateTimeOffset GetUtcNow() => new(utcNow);
        }

        private sealed class CollectingLogger<T> : ILogger<T>
        {
            public List<string> Messages { get; } = [];

            public IDisposable? BeginScope<TState>(TState state) where TState : notnull
                => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter)
                => Messages.Add(formatter(state, exception));
        }

        private sealed class FakeCaseResolver(EmailAutomationCaseMatch? match)
            : IEmailAutomationCaseResolver
        {
            public Task<EmailAutomationCaseMatch?> ResolveByCourtCaseNumberAsync(
                string courtCaseNumber,
                CancellationToken cancellationToken)
                => Task.FromResult(match);
        }
    }
}
