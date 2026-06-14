using Microsoft.EntityFrameworkCore;
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
                     x.TargetEmail == "employee@ezer-law.com");
        }

        [Fact]
        public async Task IdempotencyPreventsDuplicateForward()
        {
            await using var db = CreateDb();
            var graph = new FakeGraphClient(Message("graph-1", "<same@test>"));
            var service = CreateService(db, graph, dryRun: false, testForwardEnabled: true);
            await service.RunAsync(CancellationToken.None);

            graph.Enqueue(Message("graph-2", "<same@test>"), "delta-2");
            await service.RunAsync(CancellationToken.None);

            Assert.Single(graph.Forwards);
            Assert.Contains(
                db.EmailAutomationLogs,
                x => x.GraphMessageId == "graph-2" &&
                     x.Action == EmailAutomationActions.SkippedAlreadyProcessed);
        }

        [Fact]
        public async Task AlreadyProcessedGraphMessageIsSkipped()
        {
            await using var db = CreateDb();
            db.EmailAutomationLogs.Add(new EmailAutomationLog
            {
                Mailbox = "amir@ezer-law.com",
                GraphMessageId = "graph-1",
                Action = EmailAutomationActions.Read,
                CreatedAtUtc = BaselineUtc
            });
            await db.SaveChangesAsync();

            var graph = new FakeGraphClient(Message("graph-1", "<mail-1@test>"));
            var service = CreateService(db, graph, dryRun: false, testForwardEnabled: true);
            await service.RunAsync(CancellationToken.None);

            Assert.Empty(graph.Forwards);
            Assert.Contains(
                db.EmailAutomationLogs,
                x => x.GraphMessageId == "graph-1" &&
                     x.Action == EmailAutomationActions.SkippedAlreadyProcessed);
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
                db.EmailAutomationLogs.Where(x => x.Action == EmailAutomationActions.Read));
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
            Assert.Equal("odmon@ezer-law.com", audit.ActualForwardTo);
            Assert.Equal(456, audit.ResolvedTikCounter);
            Assert.Equal("5/2000", audit.ResolvedTikNumber);
            Assert.Equal(5, audit.ResolvedClientNumber);
            Assert.Equal("123-45-67", audit.DetectedCourtCaseNumber);
            Assert.Equal("odmon@ezer-law.com", Assert.Single(graph.Forwards).Target);
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
        public async Task NoMatchingCase_DoesNotForward_AndAuditsNoCaseMatch()
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
            var audit = Assert.Single(
                db.EmailAutomationLogs.Where(
                    x => x.Action == EmailAutomationActions.NoCaseMatch));
            Assert.Equal("123-45-67", audit.DetectedCourtCaseNumber);
            Assert.Contains("No Odcanit case", audit.ErrorMessage);
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
                     x.ResolvedTikCounter == 456);
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
            => new(
                db,
                graph,
                resolver,
                Options.Create(settings),
                NullLogger<EmailAutomationService>.Instance,
                new FixedTimeProvider(BaselineUtc));

        private static EmailAutomationSettings CreateSettings(
            bool dryRun,
            bool testForwardEnabled)
            => new()
            {
                Enabled = true,
                DryRun = dryRun,
                MaxMessagesPerCycle = 50,
                MaxForwardsPerCycle = 20,
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
                CancellationToken cancellationToken)
            {
                if (ThrottleForward)
                {
                    throw new GraphThrottledException(TimeSpan.FromMinutes(1));
                }

                Forwards.Add((mailbox, graphMessageId, targetEmail));
                return Task.CompletedTask;
            }
        }

        private sealed class FixedTimeProvider(DateTime utcNow) : TimeProvider
        {
            public override DateTimeOffset GetUtcNow() => new(utcNow);
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
