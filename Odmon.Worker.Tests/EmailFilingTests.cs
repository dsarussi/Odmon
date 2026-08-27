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
    public sealed class EmailFilingTests
    {
        private static readonly DateTime ReceivedUtc =
            new(2026, 8, 26, 9, 15, 0, DateTimeKind.Utc);

        [Theory]
        [InlineData("9/1984", "9/1984")]
        [InlineData("דחוף7/1236002", "7/1236002")]
        [InlineData("2/2387.", "2/2387")]
        [InlineData("9/681", "9/681")]
        [InlineData("23/166", "23/166")]
        public void ExtractionUsesCompleteNumericToken(string input, string expected)
        {
            var candidate = Assert.Single(
                EmailFilingService.ExtractTikNumberCandidates(input, null));

            Assert.Equal(expected, candidate.Value);
            Assert.Equal(EmailFilingConstants.SubjectSource, candidate.Source);
        }

        [Theory]
        [InlineData("15/9/27")]
        [InlineData("03/10/2022")]
        [InlineData("900/10179/1")]
        [InlineData("x15/9/27y")]
        public void ExtractionRejectsDatesAndMultiSlashPartials(string input)
        {
            Assert.Empty(EmailFilingService.ExtractTikNumberCandidates(input, null));
        }

        [Fact]
        public void HtmlNormalizationDoesNotTurnSplitMultiSlashValueIntoPartialTik()
        {
            var normalized = EmailFilingService.NormalizeBodyForDetection(
                "<span>15/9</span>/<span>27</span>",
                "html");

            Assert.Equal("15/9/27", normalized);
            Assert.Empty(EmailFilingService.ExtractTikNumberCandidates(null, normalized));
        }

        [Fact]
        public void FilingGraphSelectIncludesEmailContentButNeverAttachments()
        {
            var forwardingSelect = MicrosoftGraphEmailAutomationClient.GetMessageSelect(
                includeBody: false);
            var filingSelect = MicrosoftGraphEmailAutomationClient.GetMessageSelect(
                includeBody: true);

            Assert.DoesNotContain("body", forwardingSelect, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("bccRecipients", forwardingSelect, StringComparison.Ordinal);
            Assert.Contains("body", filingSelect, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("sender", filingSelect, StringComparison.Ordinal);
            Assert.Contains("bccRecipients", filingSelect, StringComparison.Ordinal);
            Assert.Contains("sentDateTime", filingSelect, StringComparison.Ordinal);
            Assert.DoesNotContain("attachment", filingSelect, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("$expand", filingSelect, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void ExtractionFindsMultipleTikNumbersAndTheirSources()
        {
            var candidates = EmailFilingService.ExtractTikNumberCandidates(
                "Subject 9/1984 and 253/248",
                "Body 7/1236002");

            Assert.Equal(3, candidates.Count);
            Assert.Contains(candidates, value =>
                value == new EmailFilingService.IdentifierCandidate("9/1984", "Subject"));
            Assert.Contains(candidates, value =>
                value == new EmailFilingService.IdentifierCandidate("253/248", "Subject"));
            Assert.Contains(candidates, value =>
                value == new EmailFilingService.IdentifierCandidate("7/1236002", "Body"));
        }

        [Fact]
        public async Task UnresolvedCandidateIsPersistedAsSuspectNotCase()
        {
            await using var db = CreateDb();
            var service = CreateService(db, new Dictionary<string, int>());

            var diagnostic = await service.ProcessAsync(
                "mailbox@odmon.example",
                [],
                Message("Unknown 7000/355"),
                CancellationToken.None);

            Assert.NotNull(diagnostic);
            var candidate = Assert.Single(diagnostic!.Candidates);
            Assert.Equal(EmailFilingConstants.SuspectNotCase, candidate.ResolutionStatus);
            Assert.Equal(EmailFilingConstants.NoValidTik, diagnostic.FinalDecision);
            Assert.Empty(diagnostic.Targets);
        }

        [Fact]
        public async Task OneEmailWouldFileToEveryResolvedCase()
        {
            await using var db = CreateDb();
            var graph = new FakeFilingGraphClient();
            var writer = new FakeDocumentWriter();
            var service = CreateService(db, new Dictionary<string, int>
            {
                ["9/1984"] = 40514,
                ["253/248"] = 60002
            }, graphClient: graph, documentWriter: writer);

            var diagnostic = await service.ProcessAsync(
                "mailbox@odmon.example",
                [],
                Message("Cases 9/1984 and 253/248", body: "No attachments"),
                CancellationToken.None);

            Assert.NotNull(diagnostic);
            Assert.Equal(2, diagnostic!.Targets.Count);
            Assert.All(diagnostic.Targets, target =>
                Assert.Equal(EmailFilingConstants.DryRunWouldFile, target.Decision));
            Assert.Equal(new[] { 40514, 60002 },
                diagnostic.Targets.Select(target => target.TikCounter).OrderBy(x => x));
            Assert.Equal(0, graph.MimeFetchCount);
            Assert.Empty(writer.WrittenTikCounters);
        }

        [Fact]
        public async Task ExistingEmailAndTargetDedupSkipsTarget()
        {
            await using var db = CreateDb();
            var service = CreateService(db, new Dictionary<string, int>
            {
                ["9/1984"] = 40514
            });
            var message = Message("Case 9/1984");
            var first = await service.ProcessAsync(
                "mailbox@odmon.example",
                [],
                message,
                CancellationToken.None);
            Assert.NotNull(first);
            Assert.Equal(EmailFilingConstants.DryRunWouldFile, first!.FinalDecision);
            db.EmailFilingDedups.Add(new EmailFilingDedup
            {
                MessageFingerprint = first.MessageFingerprint,
                TikNumber = "9/1984",
                TikCounter = 40514,
                FiledAtUtc = ReceivedUtc
            });
            await db.SaveChangesAsync();

            var second = await service.ProcessAsync(
                "mailbox@odmon.example",
                [],
                message,
                CancellationToken.None);

            Assert.NotNull(second);
            Assert.Equal(EmailFilingConstants.AllTargetsDuplicate, second!.FinalDecision);
            Assert.Equal(EmailFilingConstants.SkipDuplicate, Assert.Single(second.Targets).Decision);
        }

        [Fact]
        public async Task InternetMessageIdentityDeduplicatesAcrossMonitoredMailboxes()
        {
            await using var db = CreateDb();
            var service = CreateService(db, new Dictionary<string, int>
            {
                ["9/1984"] = 40514
            });
            var message = Message("9/1984");
            var first = await service.ProcessAsync(
                "first@odmon.example",
                [],
                message,
                CancellationToken.None);
            Assert.NotNull(first);
            db.EmailFilingDedups.Add(new EmailFilingDedup
            {
                MessageFingerprint = first!.MessageFingerprint,
                TikNumber = "9/1984",
                TikCounter = 40514,
                FiledAtUtc = ReceivedUtc
            });
            await db.SaveChangesAsync();

            var second = await service.ProcessAsync(
                "second@odmon.example",
                [],
                message,
                CancellationToken.None);

            Assert.NotNull(second);
            Assert.Equal(first.MessageFingerprint, second!.MessageFingerprint);
            Assert.Equal(EmailFilingConstants.AllTargetsDuplicate, second.FinalDecision);
        }

        [Theory]
        [InlineData("RE: Update for 9/1984")]
        [InlineData("FW: Update for 9/1984")]
        [InlineData("FWD: Update for 9/1984")]
        public async Task ReplyAndForwardSubjectsRemainEligibleForFiling(string subject)
        {
            await using var db = CreateDb();
            var service = CreateService(db, new Dictionary<string, int>
            {
                ["9/1984"] = 40514
            });

            var diagnostic = await service.ProcessAsync(
                "mailbox@odmon.example",
                [],
                Message(subject),
                CancellationToken.None);

            Assert.NotNull(diagnostic);
            Assert.Equal(EmailFilingConstants.DryRunWouldFile, diagnostic!.FinalDecision);
            Assert.Single(diagnostic.Targets);
        }

        [Fact]
        public async Task InlineImagesScriptsAndAttachmentsAreNotIngestedOrScanned()
        {
            await using var db = CreateDb();
            var service = CreateService(db, new Dictionary<string, int>
            {
                ["9/1984"] = 40514
            });
            const string html = """
                <html><body>
                <img alt="signature 7/777" src="data:image/png;base64,900/999" />
                <script>const hidden = '8/888';</script>
                Visible case <b>9/1984</b>
                </body></html>
                """;

            var diagnostic = await service.ProcessAsync(
                "mailbox@odmon.example",
                [],
                Message("No case in subject", html, "html"),
                CancellationToken.None);

            Assert.NotNull(diagnostic);
            var tikCandidate = Assert.Single(diagnostic!.Candidates.Where(candidate =>
                candidate.CandidateType == EmailFilingConstants.TikCandidateType));
            Assert.Equal("9/1984", tikCandidate.Candidate);
            Assert.DoesNotContain(db.EmailFilingCandidateDiagnostics, candidate =>
                candidate.Candidate is "7/777" or "8/888" or "900/999");
        }

        [Theory]
        [InlineData(40514, "AGREEMENT")]
        [InlineData(99999, "CONFLICT")]
        public async Task CourtObserverComparesWithoutAuthorizingFiling(
            int courtTikCounter,
            string expectedClassification)
        {
            await using var db = CreateDb();
            var courtResolver = new FakeCourtResolver(new Dictionary<string, EmailAutomationCaseMatch>
            {
                ["123-45-67"] = new(courtTikCounter, "court/tik", null)
            });
            var service = CreateService(
                db,
                new Dictionary<string, int> { ["9/1984"] = 40514 },
                courtResolver: courtResolver);
            var rules = new[]
            {
                new EmailAutomationRuleSettings
                {
                    Enabled = true,
                    SubjectRegex = @"\b\d{1,6}-\d{2}-\d{2}\b"
                }
            };

            var diagnostic = await service.ProcessAsync(
                "mailbox@odmon.example",
                rules,
                Message("Tik 9/1984, court 123-45-67"),
                CancellationToken.None);

            Assert.NotNull(diagnostic);
            Assert.Contains(expectedClassification, diagnostic!.ObserverClassifications);
            var target = Assert.Single(diagnostic.Targets);
            Assert.Equal(40514, target.TikCounter);
            Assert.Equal(EmailFilingConstants.DryRunWouldFile, target.Decision);
        }

        [Fact]
        public async Task CourtOnlyResolutionNeverCreatesFilingTarget()
        {
            await using var db = CreateDb();
            var courtResolver = new FakeCourtResolver(new Dictionary<string, EmailAutomationCaseMatch>
            {
                ["123-45-67"] = new(99999, "7/777", null)
            });
            var service = CreateService(
                db,
                new Dictionary<string, int>(),
                dryRun: false,
                realWriteEnabled: true,
                courtResolver: courtResolver);
            var rules = new[]
            {
                new EmailAutomationRuleSettings
                {
                    Enabled = true,
                    SubjectRegex = @"\b\d{1,6}-\d{2}-\d{2}\b"
                }
            };

            var diagnostic = await service.ProcessAsync(
                "mailbox@odmon.example",
                rules,
                Message("Court 123-45-67"),
                CancellationToken.None);

            Assert.NotNull(diagnostic);
            Assert.Contains("COURT_ONLY", diagnostic!.ObserverClassifications);
            Assert.Equal(EmailFilingConstants.NoTikCandidates, diagnostic.FinalDecision);
            Assert.Empty(diagnostic.Targets);
            Assert.Empty(db.EmailFilingDedups);
        }

        [Fact]
        public async Task ExactTikNumberWritesOneMsgDocumentOnce()
        {
            await using var db = CreateDb();
            var graph = new FakeFilingGraphClient();
            var generator = new FakeMsgGenerator();
            var writer = new FakeDocumentWriter();
            var service = CreateService(
                db,
                new Dictionary<string, int> { ["9/1984"] = 40514 },
                dryRun: false,
                realWriteEnabled: true,
                graphClient: graph,
                msgGenerator: generator,
                documentWriter: writer);

            var diagnostic = await service.ProcessAsync(
                "mailbox@odmon.example",
                [],
                Message("9/1984"),
                CancellationToken.None);

            Assert.NotNull(diagnostic);
            Assert.Equal(EmailFilingConstants.Filed, diagnostic!.FinalDecision);
            Assert.Equal(1, graph.MimeFetchCount);
            Assert.Equal(1, generator.GenerateCount);
            Assert.Equal([40514], writer.WrittenTikCounters);
            Assert.Single(db.EmailFilingDedups);
            Assert.True(generator.LastArtifact!.Disposed);
        }

        [Fact]
        public async Task NonAllowlistedResolvedCaseDoesNotWrite()
        {
            await using var db = CreateDb();
            var graph = new FakeFilingGraphClient();
            var writer = new FakeDocumentWriter();
            var service = CreateService(
                db,
                new Dictionary<string, int> { ["253/248"] = 60002 },
                dryRun: false,
                realWriteEnabled: true,
                graphClient: graph,
                documentWriter: writer);

            var diagnostic = await service.ProcessAsync(
                "mailbox@odmon.example",
                [],
                Message("253/248"),
                CancellationToken.None);

            Assert.NotNull(diagnostic);
            Assert.Equal(EmailFilingConstants.NotAllowlisted, diagnostic!.FinalDecision);
            Assert.Empty(db.EmailFilingDedups);
            Assert.Equal(0, graph.MimeFetchCount);
            Assert.Empty(writer.WrittenTikCounters);
        }

        [Fact]
        public async Task TwoAllowlistedCasesFetchAndGenerateOnceThenWriteTwice()
        {
            await using var db = CreateDb();
            var graph = new FakeFilingGraphClient();
            var generator = new FakeMsgGenerator();
            var writer = new FakeDocumentWriter();
            var service = CreateService(
                db,
                new Dictionary<string, int>
                {
                    ["9/1984"] = 40514,
                    ["253/248"] = 60002
                },
                dryRun: false,
                realWriteEnabled: true,
                allowlist:
                [
                    new EmailFilingAllowlistEntry { TikNumber = "9/1984", TikCounter = 40514 },
                    new EmailFilingAllowlistEntry { TikNumber = "253/248", TikCounter = 60002 }
                ],
                graphClient: graph,
                msgGenerator: generator,
                documentWriter: writer);

            var diagnostic = await service.ProcessAsync(
                "mailbox@odmon.example",
                [],
                Message("9/1984 and 253/248"),
                CancellationToken.None);

            Assert.NotNull(diagnostic);
            Assert.Equal(EmailFilingConstants.Filed, diagnostic!.FinalDecision);
            Assert.Equal(new[] { 40514, 60002 },
                diagnostic.Targets.Select(target => target.TikCounter).OrderBy(value => value));
            Assert.All(diagnostic.Targets, target =>
                Assert.Equal(EmailFilingConstants.Filed, target.Decision));
            Assert.Equal(1, graph.MimeFetchCount);
            Assert.Equal(1, generator.GenerateCount);
            Assert.Equal(new[] { 40514, 60002 }, writer.WrittenTikCounters.OrderBy(value => value));
            Assert.Equal(2, db.EmailFilingDedups.Count());
            Assert.True(generator.LastArtifact!.Disposed);
        }

        [Fact]
        public async Task SuccessfulEmailTargetIsDeduplicatedOnRetry()
        {
            await using var db = CreateDb();
            var graph = new FakeFilingGraphClient();
            var generator = new FakeMsgGenerator();
            var writer = new FakeDocumentWriter();
            var service = CreateService(
                db,
                new Dictionary<string, int> { ["9/1984"] = 40514 },
                dryRun: false,
                realWriteEnabled: true,
                graphClient: graph,
                msgGenerator: generator,
                documentWriter: writer);
            var message = Message("9/1984");

            var first = await service.ProcessAsync("mailbox@odmon.example", [], message, CancellationToken.None);
            var second = await service.ProcessAsync("mailbox@odmon.example", [], message, CancellationToken.None);

            Assert.Equal(EmailFilingConstants.Filed, first!.FinalDecision);
            Assert.Equal(EmailFilingConstants.AllTargetsDuplicate, second!.FinalDecision);
            Assert.Equal(1, graph.MimeFetchCount);
            Assert.Equal(1, generator.GenerateCount);
            Assert.Equal([40514], writer.WrittenTikCounters);
            Assert.Single(db.EmailFilingDedups);
        }

        [Fact]
        public async Task OneDuplicateTargetAndOneNewTargetWritesOnlyNewTarget()
        {
            await using var db = CreateDb();
            var resolutions = new Dictionary<string, int>
            {
                ["9/1984"] = 40514,
                ["253/248"] = 60002
            };
            var message = Message("9/1984 and 253/248");
            var observer = CreateService(db, resolutions);
            var observed = await observer.ProcessAsync(
                "mailbox@odmon.example", [], message, CancellationToken.None);
            db.EmailFilingDedups.Add(new EmailFilingDedup
            {
                MessageFingerprint = observed!.MessageFingerprint,
                TikNumber = "9/1984",
                TikCounter = 40514,
                FiledAtUtc = ReceivedUtc
            });
            await db.SaveChangesAsync();

            var graph = new FakeFilingGraphClient();
            var generator = new FakeMsgGenerator();
            var writer = new FakeDocumentWriter();
            var service = CreateService(
                db,
                resolutions,
                dryRun: false,
                realWriteEnabled: true,
                allowlist:
                [
                    new EmailFilingAllowlistEntry { TikNumber = "9/1984", TikCounter = 40514 },
                    new EmailFilingAllowlistEntry { TikNumber = "253/248", TikCounter = 60002 }
                ],
                graphClient: graph,
                msgGenerator: generator,
                documentWriter: writer);

            var diagnostic = await service.ProcessAsync(
                "mailbox@odmon.example", [], message, CancellationToken.None);

            Assert.Equal(EmailFilingConstants.Filed, diagnostic!.FinalDecision);
            Assert.Equal(EmailFilingConstants.SkipDuplicate,
                diagnostic.Targets.Single(value => value.TikCounter == 40514).Decision);
            Assert.Equal(EmailFilingConstants.Filed,
                diagnostic.Targets.Single(value => value.TikCounter == 60002).Decision);
            Assert.Equal([60002], writer.WrittenTikCounters);
            Assert.Equal(1, graph.MimeFetchCount);
            Assert.Equal(1, generator.GenerateCount);
            Assert.Equal(2, db.EmailFilingDedups.Count());
        }

        [Fact]
        public async Task MimeGenerationFailureWritesNothingAndCreatesNoSuccessDedup()
        {
            await using var db = CreateDb();
            var graph = new FakeFilingGraphClient();
            var generator = new FakeMsgGenerator { ThrowOnGenerate = true };
            var writer = new FakeDocumentWriter();
            var service = CreateService(
                db,
                new Dictionary<string, int> { ["9/1984"] = 40514 },
                dryRun: false,
                realWriteEnabled: true,
                graphClient: graph,
                msgGenerator: generator,
                documentWriter: writer);

            await Assert.ThrowsAsync<EmailFilingProcessingException>(() => service.ProcessAsync(
                "mailbox@odmon.example", [], Message("9/1984"), CancellationToken.None));

            Assert.Equal(1, graph.MimeFetchCount);
            Assert.Empty(writer.WrittenTikCounters);
            Assert.Empty(db.EmailFilingDedups);
            Assert.Equal(EmailFilingConstants.MimeFailed,
                Assert.Single(db.EmailFilingTargetDiagnostics).Decision);
        }

        [Fact]
        public async Task MsgPreflightFailureOccursBeforeReservationOrOdcanitRowCreation()
        {
            await using var db = CreateDb();
            var writer = new FakeDocumentWriter { ThrowOnPreflight = true };
            var service = CreateService(
                db,
                new Dictionary<string, int> { ["9/1984"] = 40514 },
                dryRun: false,
                realWriteEnabled: true,
                documentWriter: writer);

            await Assert.ThrowsAsync<EmailFilingProcessingException>(() => service.ProcessAsync(
                "mailbox@odmon.example", [], Message("9/1984"), CancellationToken.None));

            Assert.Empty(db.EmailFilingDedups);
            Assert.Empty(writer.CreatedTikCounters);
            Assert.Empty(writer.WrittenTikCounters);
            Assert.Equal(EmailFilingConstants.MimeFailed,
                Assert.Single(db.EmailFilingTargetDiagnostics).Decision);
        }

        [Fact]
        public async Task CopyVerificationFailureIsRetryableAndCleansTemporaryArtifact()
        {
            await using var db = CreateDb();
            var graph = new FakeFilingGraphClient();
            var generator = new FakeMsgGenerator();
            var writer = new FakeDocumentWriter();
            var notifier = new FakeEmailNotifier();
            writer.FailTikCounters.Add(40514);
            var service = CreateService(
                db,
                new Dictionary<string, int> { ["9/1984"] = 40514 },
                dryRun: false,
                realWriteEnabled: true,
                graphClient: graph,
                msgGenerator: generator,
                documentWriter: writer,
                emailNotifier: notifier);
            var message = Message("9/1984");

            await Assert.ThrowsAsync<EmailFilingProcessingException>(() => service.ProcessAsync(
                "mailbox@odmon.example", [], message, CancellationToken.None));
            var failedState = Assert.Single(db.EmailFilingDedups);
            Assert.Equal(EmailFilingWriteStates.CopyFailed, failedState.Status);
            Assert.NotNull(failedState.OdcanitDocCounter);
            Assert.False(string.IsNullOrWhiteSpace(failedState.OdcanitDestPath));
            Assert.Single(notifier.CriticalAlertBodies);
            Assert.DoesNotContain("9/1984", notifier.CriticalAlertBodies[0]);
            Assert.DoesNotContain("synthetic-1", notifier.CriticalAlertBodies[0]);
            Assert.True(generator.LastArtifact!.Disposed);

            writer.FailTikCounters.Clear();
            var retry = await service.ProcessAsync(
                "mailbox@odmon.example", [], message, CancellationToken.None);

            Assert.Equal(EmailFilingConstants.Filed, retry!.FinalDecision);
            Assert.Equal([40514], writer.WrittenTikCounters);
            Assert.Single(db.EmailFilingDedups);
            Assert.Equal([40514], writer.CreatedTikCounters);
            Assert.Equal(EmailFilingWriteStates.Succeeded, failedState.Status);
            Assert.Equal(2, graph.MimeFetchCount);
            Assert.Equal(2, generator.GenerateCount);
            Assert.True(generator.LastArtifact!.Disposed);
        }

        [Fact]
        public async Task MultiTargetFailureMarksOnlySuccessfulTargetAsDeduplicated()
        {
            await using var db = CreateDb();
            var graph = new FakeFilingGraphClient();
            var generator = new FakeMsgGenerator();
            var writer = new FakeDocumentWriter();
            writer.FailTikCounters.Add(60002);
            var service = CreateService(
                db,
                new Dictionary<string, int>
                {
                    ["9/1984"] = 40514,
                    ["253/248"] = 60002
                },
                dryRun: false,
                realWriteEnabled: true,
                allowlist:
                [
                    new EmailFilingAllowlistEntry { TikNumber = "9/1984", TikCounter = 40514 },
                    new EmailFilingAllowlistEntry { TikNumber = "253/248", TikCounter = 60002 }
                ],
                graphClient: graph,
                msgGenerator: generator,
                documentWriter: writer);

            await Assert.ThrowsAsync<EmailFilingProcessingException>(() => service.ProcessAsync(
                "mailbox@odmon.example", [], Message("9/1984 and 253/248"), CancellationToken.None));

            var success = Assert.Single(db.EmailFilingDedups.Where(
                row => row.Status == EmailFilingWriteStates.Succeeded));
            Assert.Equal(40514, success.TikCounter);
            Assert.Equal(2, db.EmailFilingDedups.Count());
            Assert.Equal(EmailFilingWriteStates.CopyFailed,
                db.EmailFilingDedups.Single(row => row.TikCounter == 60002).Status);
            Assert.Equal(EmailFilingConstants.Filed,
                db.EmailFilingTargetDiagnostics.Single(value => value.TikCounter == 40514).Decision);
            Assert.Equal(EmailFilingConstants.WriteFailed,
                db.EmailFilingTargetDiagnostics.Single(value => value.TikCounter == 60002).Decision);
            Assert.Equal(1, graph.MimeFetchCount);
            Assert.Equal(1, generator.GenerateCount);
            Assert.True(generator.LastArtifact!.Disposed);
        }

        [Fact]
        public async Task CopyingStateWithVerifiedDestinationFinalizesWithoutNewOdcanitRowOrCopy()
        {
            await using var db = CreateDb();
            var message = Message("9/1984");
            var observer = CreateService(db, new Dictionary<string, int> { ["9/1984"] = 40514 });
            var observed = await observer.ProcessAsync(
                "mailbox@odmon.example", [], message, CancellationToken.None);
            const string destination = @"C:\synthetic\40514.msg";
            db.EmailFilingDedups.Add(new EmailFilingDedup
            {
                MessageFingerprint = observed!.MessageFingerprint,
                TikNumber = "9/1984",
                TikCounter = 40514,
                Status = EmailFilingWriteStates.Copying,
                OdcanitDocCounter = 140514,
                OdcanitDestPath = destination,
                ExpectedFileLength = 4096,
                CreatedAtUtc = ReceivedUtc,
                UpdatedAtUtc = ReceivedUtc
            });
            await db.SaveChangesAsync();

            var writer = new FakeDocumentWriter();
            writer.VerifiedDestinations.Add(destination);
            var service = CreateService(
                db,
                new Dictionary<string, int> { ["9/1984"] = 40514 },
                dryRun: false,
                realWriteEnabled: true,
                documentWriter: writer);

            var diagnostic = await service.ProcessAsync(
                "mailbox@odmon.example", [], message, CancellationToken.None);

            Assert.Equal(EmailFilingConstants.Filed, diagnostic!.FinalDecision);
            Assert.Empty(writer.CreatedTikCounters);
            Assert.Empty(writer.WrittenTikCounters);
            var state = Assert.Single(db.EmailFilingDedups);
            Assert.Equal(EmailFilingWriteStates.Succeeded, state.Status);
            Assert.NotNull(state.FiledAtUtc);
        }

        [Fact]
        public async Task CreatingDocumentStateRequiresManualRepairAndNeverBlindlyRetriesSp()
        {
            await using var db = CreateDb();
            var message = Message("9/1984");
            var observer = CreateService(db, new Dictionary<string, int> { ["9/1984"] = 40514 });
            var observed = await observer.ProcessAsync(
                "mailbox@odmon.example", [], message, CancellationToken.None);
            db.EmailFilingDedups.Add(new EmailFilingDedup
            {
                MessageFingerprint = observed!.MessageFingerprint,
                TikNumber = "9/1984",
                TikCounter = 40514,
                Status = EmailFilingWriteStates.CreatingDocument,
                ExpectedFileLength = 4096,
                CreatedAtUtc = ReceivedUtc,
                UpdatedAtUtc = ReceivedUtc
            });
            await db.SaveChangesAsync();

            var writer = new FakeDocumentWriter();
            var notifier = new FakeEmailNotifier();
            var service = CreateService(
                db,
                new Dictionary<string, int> { ["9/1984"] = 40514 },
                dryRun: false,
                realWriteEnabled: true,
                documentWriter: writer,
                emailNotifier: notifier);

            await Assert.ThrowsAsync<EmailFilingProcessingException>(() => service.ProcessAsync(
                "mailbox@odmon.example", [], message, CancellationToken.None));

            Assert.Empty(writer.CreatedTikCounters);
            Assert.Empty(writer.WrittenTikCounters);
            Assert.Single(notifier.CriticalAlertBodies);
            Assert.DoesNotContain("9/1984", notifier.CriticalAlertBodies[0]);
            Assert.DoesNotContain("synthetic-1", notifier.CriticalAlertBodies[0]);
            Assert.Equal(
                EmailFilingConstants.ManualRepairRequired,
                db.EmailFilingDiagnostics.OrderBy(row => row.Id).Last().FinalDecision);
        }

        [Fact]
        public void HebrewSubjectUsesExistingSafeDocumentNameRules()
        {
            var name = EmailFilingDocumentWriter.BuildDocumentName(
                "בדיקת מייל 9/1984: עדכון?*");

            Assert.Contains("בדיקת מייל", name);
            Assert.DoesNotContain(':', name);
            Assert.DoesNotContain('?', name);
            Assert.DoesNotContain('*', name);
            Assert.True(name.Length <= 180);
        }

        [Fact]
        public async Task RealWriteDisabledHasNoSideEffect()
        {
            await using var db = CreateDb();
            var graph = new FakeFilingGraphClient();
            var writer = new FakeDocumentWriter();
            var service = CreateService(
                db,
                new Dictionary<string, int> { ["9/1984"] = 40514 },
                dryRun: false,
                realWriteEnabled: false,
                graphClient: graph,
                documentWriter: writer);

            var diagnostic = await service.ProcessAsync(
                "mailbox@odmon.example",
                [],
                Message("9/1984"),
                CancellationToken.None);

            Assert.NotNull(diagnostic);
            Assert.Equal(EmailFilingConstants.RealWriteDisabled, diagnostic!.FinalDecision);
            Assert.Empty(db.EmailFilingDedups);
            Assert.Equal(0, graph.MimeFetchCount);
            Assert.Empty(writer.WrittenTikCounters);
        }

        [Fact]
        public void ObserverClassificationExposesMultiAndPartialOverlap()
        {
            var classifications = EmailFilingService.ClassifyObserverResults(
                [1, 2],
                [2, 3]);

            Assert.Equal(
                new[] { "PARTIAL_OVERLAP", "MULTI_TIK", "MULTI_COURT" },
                classifications);
        }

        [Fact]
        public async Task PollingUsesIndependentCursorAndLeavesForwardingStateUntouched()
        {
            await using var db = CreateDb();
            var filingSettings = new EmailFilingSettings
            {
                Enabled = true,
                DryRun = true,
                StartProcessingFromUtc = ReceivedUtc.AddMinutes(-1),
                MaxMessagesPerCycle = 10
            };
            var mailbox = new EmailAutomationMailboxSettings
            {
                Address = "mailbox@odmon.example",
                InboxFolder = "Inbox"
            };
            var automationSettings = new EmailAutomationSettings
            {
                FingerprintKey = "synthetic-email-filing-test-key",
                Mailboxes = [mailbox]
            };
            var filingService = new EmailFilingService(
                db,
                new FakeOdcanitReader(new Dictionary<string, int> { ["9/1984"] = 40514 }),
                new FakeCourtResolver(new Dictionary<string, EmailAutomationCaseMatch>()),
                new FakeFilingGraphClient(Message("RE: Update 9/1984")),
                new FakeMsgGenerator(),
                new FakeDocumentWriter(),
                Options.Create(filingSettings),
                Options.Create(automationSettings),
                NullLogger<EmailFilingService>.Instance,
                new FakeEmailNotifier(),
                new FixedTimeProvider(ReceivedUtc.AddMinutes(1)));
            var polling = new EmailFilingPollingService(
                db,
                new FakeFilingGraphClient(Message("RE: Update 9/1984")),
                filingService,
                Options.Create(filingSettings),
                Options.Create(automationSettings),
                NullLogger<EmailFilingPollingService>.Instance,
                new FixedTimeProvider(ReceivedUtc.AddMinutes(1)));

            await polling.RunAsync(CancellationToken.None);

            Assert.Empty(db.EmailAutomationMailboxStates);
            var state = Assert.Single(db.EmailFilingMailboxStates);
            Assert.Equal("filing-delta-1", state.DeltaLink);
            Assert.NotNull(state.LastSuccessfulSyncUtc);
            var diagnostic = Assert.Single(db.EmailFilingDiagnostics);
            Assert.Equal(EmailFilingConstants.DryRunWouldFile, diagnostic.FinalDecision);
        }

        private static EmailFilingService CreateService(
            IntegrationDbContext db,
            IReadOnlyDictionary<string, int> resolvedTikNumbers,
            bool dryRun = true,
            bool realWriteEnabled = false,
            IEmailAutomationCaseResolver? courtResolver = null,
            IReadOnlyList<EmailFilingAllowlistEntry>? allowlist = null,
            IEmailFilingGraphClient? graphClient = null,
            IEmailMsgGenerator? msgGenerator = null,
            IEmailFilingDocumentWriter? documentWriter = null,
            IEmailNotifier? emailNotifier = null)
        {
            var filingSettings = new EmailFilingSettings
            {
                Enabled = true,
                DryRun = dryRun,
                RealWriteEnabled = realWriteEnabled
            };
            if (allowlist != null)
            {
                filingSettings.RealWriteAllowlist = allowlist.ToList();
            }
            var automationSettings = new EmailAutomationSettings
            {
                FingerprintKey = "synthetic-email-filing-test-key"
            };
            return new EmailFilingService(
                db,
                new FakeOdcanitReader(resolvedTikNumbers),
                courtResolver ?? new FakeCourtResolver(new Dictionary<string, EmailAutomationCaseMatch>()),
                graphClient ?? new FakeFilingGraphClient(),
                msgGenerator ?? new FakeMsgGenerator(),
                documentWriter ?? new FakeDocumentWriter(),
                Options.Create(filingSettings),
                Options.Create(automationSettings),
                NullLogger<EmailFilingService>.Instance,
                emailNotifier ?? new FakeEmailNotifier(),
                new FixedTimeProvider(ReceivedUtc));
        }

        private static EmailAutomationMessage Message(
            string subject,
            string? body = "Synthetic body",
            string? bodyContentType = "text")
            => new(
                "graph-synthetic-1",
                "<synthetic-1@odmon.example>",
                subject,
                "sender@odmon.example",
                ["recipient@odmon.example"],
                ["copy@odmon.example"],
                ReceivedUtc,
                Body: body,
                BodyContentType: bodyContentType,
                BccRecipients: ["blind@odmon.example"]);

        private static IntegrationDbContext CreateDb()
        {
            var options = new DbContextOptionsBuilder<IntegrationDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options;
            return new IntegrationDbContext(options);
        }

        private sealed class FakeOdcanitReader(IReadOnlyDictionary<string, int> resolutions)
            : IOdcanitReader
        {
            public Task<Dictionary<string, int>> ResolveTikNumbersToCountersAsync(
                IEnumerable<string> tikNumbers,
                CancellationToken ct)
                => Task.FromResult(tikNumbers
                    .Where(resolutions.ContainsKey)
                    .ToDictionary(value => value, value => resolutions[value], StringComparer.Ordinal));

            public Task<List<OdcanitCase>> GetCasesCreatedOnDateAsync(DateTime date, CancellationToken ct)
                => Task.FromResult(new List<OdcanitCase>());

            public Task<List<OdcanitCase>> GetCasesByTikCountersAsync(IEnumerable<int> tikCounters, CancellationToken ct)
                => Task.FromResult(new List<OdcanitCase>());

            public Task<List<OdcanitDiaryEvent>> GetDiaryEventsByTikCountersAsync(IEnumerable<int> tikCounters, CancellationToken ct)
                => Task.FromResult(new List<OdcanitDiaryEvent>());

            public Task<List<int>> GetTikCountersSinceCutoffAsync(DateTime cutoffDate, CancellationToken ct)
                => Task.FromResult(new List<int>());
        }

        private sealed class FakeCourtResolver(
            IReadOnlyDictionary<string, EmailAutomationCaseMatch> resolutions)
            : IEmailAutomationCaseResolver
        {
            public Task<EmailAutomationCaseMatch?> ResolveByCourtCaseNumberAsync(
                string courtCaseNumber,
                CancellationToken cancellationToken)
                => Task.FromResult(
                    resolutions.TryGetValue(courtCaseNumber, out var match) ? match : null);
        }

        private sealed class FakeFilingGraphClient(params EmailAutomationMessage[] messages)
            : IEmailFilingGraphClient
        {
            public int MimeFetchCount { get; private set; }
            public byte[] MimeBytes { get; set; } = [1, 2, 3];

            public Task<EmailAutomationDeltaPage> GetDeltaPageAsync(
                string mailbox,
                string folderId,
                string? deltaLink,
                DateTime processingFromUtc,
                int pageSize,
                CancellationToken cancellationToken)
                => Task.FromResult(new EmailAutomationDeltaPage(
                    messages,
                    NextLink: null,
                    DeltaLink: "filing-delta-1"));

            public Task<byte[]> GetMimeAsync(
                string mailbox,
                string messageId,
                CancellationToken cancellationToken)
            {
                MimeFetchCount++;
                return Task.FromResult(MimeBytes);
            }
        }

        private sealed class FakeMsgGenerator : IEmailMsgGenerator
        {
            public int GenerateCount { get; private set; }
            public bool ThrowOnGenerate { get; set; }
            public FakeMsgArtifact? LastArtifact { get; private set; }

            public Task<IEmailMsgArtifact> GenerateAsync(
                byte[] mimeBytes,
                CancellationToken cancellationToken)
            {
                GenerateCount++;
                if (ThrowOnGenerate)
                    throw new InvalidDataException("Synthetic MSG generation failure.");
                LastArtifact = new FakeMsgArtifact();
                return Task.FromResult<IEmailMsgArtifact>(LastArtifact);
            }
        }

        private sealed class FakeMsgArtifact : IEmailMsgArtifact
        {
            public string FilePath { get; } = Path.Combine(Path.GetTempPath(), "synthetic.msg");
            public bool Disposed { get; private set; }

            public ValueTask DisposeAsync()
            {
                Disposed = true;
                return ValueTask.CompletedTask;
            }
        }

        private sealed class FakeDocumentWriter : IEmailFilingDocumentWriter
        {
            public List<int> WrittenTikCounters { get; } = [];
            public List<int> CreatedTikCounters { get; } = [];
            public HashSet<int> FailTikCounters { get; } = [];
            public HashSet<string> VerifiedDestinations { get; } = [];
            public long ExpectedLength { get; set; } = 4096;
            public bool ThrowOnPreflight { get; set; }

            public Task<long> PreflightAsync(
                string msgFilePath,
                CancellationToken cancellationToken)
                => ThrowOnPreflight
                    ? throw new InvalidDataException("Synthetic preflight failure.")
                    : Task.FromResult(ExpectedLength);

            public Task<EmailFilingDocumentDestination> CreateDocumentRowAsync(
                int tikCounter,
                string subject,
                string msgFilePath,
                DateTime emailDateUtc,
                CancellationToken cancellationToken)
            {
                CreatedTikCounters.Add(tikCounter);
                return Task.FromResult(new EmailFilingDocumentDestination(
                    tikCounter + 100000,
                    $@"C:\synthetic\{tikCounter}.msg"));
            }

            public Task<bool> IsVerifiedDestinationAsync(
                string destinationPath,
                long expectedLength,
                CancellationToken cancellationToken)
                => Task.FromResult(VerifiedDestinations.Contains(destinationPath));

            public Task CopyAndVerifyAsync(
                string msgFilePath,
                string destinationPath,
                long expectedLength,
                CancellationToken cancellationToken)
            {
                var tikCounter = int.Parse(Path.GetFileNameWithoutExtension(destinationPath));
                if (FailTikCounters.Contains(tikCounter))
                    throw new IOException("Synthetic copy verification failure.");
                WrittenTikCounters.Add(tikCounter);
                VerifiedDestinations.Add(destinationPath);
                return Task.CompletedTask;
            }
        }

        private sealed class FakeEmailNotifier : IEmailNotifier
        {
            public List<string> CriticalAlertBodies { get; } = [];

            public void QueueCriticalAlert(
                string subject,
                string body,
                string? exceptionType = null,
                string? source = null,
                string? alertType = null,
                string? environmentName = null,
                string? serverName = null)
                => CriticalAlertBodies.Add(body);

            public bool QueueEmail(
                string subject,
                string body,
                IReadOnlyCollection<string> recipients,
                bool isHtml = false,
                IReadOnlyCollection<EmailAttachmentDescriptor>? attachments = null,
                IReadOnlyCollection<string>? bccRecipients = null)
                => true;

            public Task SendDailySummaryAsync(string subject, string htmlBody, CancellationToken ct)
                => Task.CompletedTask;

            public Task SendDigestAsync(string subject, string htmlBody, CancellationToken ct)
                => Task.CompletedTask;
        }

        private sealed class FixedTimeProvider(DateTime utcNow) : TimeProvider
        {
            public override DateTimeOffset GetUtcNow() => new(utcNow);
        }
    }
}
