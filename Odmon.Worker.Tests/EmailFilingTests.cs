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
        public void ExtractionRejectsOverlongOperationalIdentifier()
        {
            var input = $"{new string('1', 32)}/{new string('2', 32)}";

            Assert.Empty(EmailFilingService.ExtractTikNumberCandidates(input, null));
        }

        [Fact]
        public void ExtractionFailsClosedWhenCandidateCountExceedsLimit()
        {
            Assert.Throws<InvalidDataException>(() =>
                EmailFilingService.ExtractTikNumberCandidates(
                    "1/1 2/2 3/3",
                    null,
                    maximumCandidates: 2));
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

        [Theory]
        [InlineData("https://graph.microsoft.com/v1.0/users/mailbox%40odmon.example/mailFolders/Inbox/messages/delta?$deltatoken=opaque")]
        [InlineData("https://graph.microsoft.com/v1.0/users/mailbox@odmon.example/mailFolders/Inbox/messages/delta?$skiptoken=opaque%2Bvalue%2Fpart%3D")]
        [InlineData("https://graph.microsoft.com/v1.0/users/mailbox%40odmon.example/mailFolders/Inbox/messages/delta?%24deltaToken=opaque%252Fstate")]
        [InlineData("https://graph.microsoft.com/v1.0/users/mailbox@odmon.example/mailFolders('Inbox')/messages/delta?$deltatoken=opaque")]
        [InlineData("https://graph.microsoft.com/v1.0/users('mailbox%40odmon.example')/mailfolders('Inbox')/messages/delta?$select=subject&%24skipToken=opaque")]
        public void GraphDeltaCursorAcceptsLegitimateGraphVariants(string cursor)
        {
            var graphBase = new Uri("https://graph.microsoft.com/v1.0/");

            Assert.Equal(
                new Uri(cursor).AbsoluteUri,
                MicrosoftGraphEmailAutomationClient.ValidateDeltaCursorUrl(
                    cursor, graphBase, "mailbox@odmon.example", "Inbox"));
        }

        [Theory]
        [InlineData("http://graph.microsoft.com/v1.0/users/mailbox%40odmon.example/mailFolders/Inbox/messages/delta?$deltatoken=opaque", GraphDeltaCursorValidationReason.InvalidScheme)]
        [InlineData("https://untrusted.example/v1.0/users/mailbox%40odmon.example/mailFolders/Inbox/messages/delta?$deltatoken=opaque", GraphDeltaCursorValidationReason.OriginMismatch)]
        [InlineData("https://graph.microsoft.com/v1.0/users/other%40odmon.example/mailFolders/Inbox/messages/delta?$deltatoken=opaque", GraphDeltaCursorValidationReason.MailboxMismatch)]
        [InlineData("https://graph.microsoft.com/v1.0/users/mailbox%40odmon.example/mailFolders/Archive/messages/delta?$deltatoken=opaque", GraphDeltaCursorValidationReason.FolderMismatch)]
        [InlineData("https://graph.microsoft.com/v1.0/users/mailbox%40odmon.example/mailFolders/Inbox/messages/other?$deltatoken=opaque", GraphDeltaCursorValidationReason.ResourcePathMismatch)]
        [InlineData("https://graph.microsoft.com/v1.0/users/mailbox%40odmon.example/mailFolders/Inbox/messages/delta/%2E%2E/other?$deltatoken=opaque", GraphDeltaCursorValidationReason.ResourcePathMismatch)]
        [InlineData("https://untrusted.example@graph.microsoft.com/v1.0/users/mailbox%40odmon.example/mailFolders/Inbox/messages/delta?$deltatoken=opaque", GraphDeltaCursorValidationReason.UserInfoNotAllowed)]
        [InlineData("https://graph.microsoft.com/v1.0/users/mailbox%40odmon.example/mailFolders/Inbox/messages/delta", GraphDeltaCursorValidationReason.MissingOpaqueToken)]
        [InlineData("https://graph.microsoft.com/v1.0/users/mailbox%40odmon.example/mailFolders/Inbox/messages/delta?$deltatoken=", GraphDeltaCursorValidationReason.MissingOpaqueToken)]
        [InlineData("https://graph.microsoft.com/v1.0/users/mailbox%40odmon.example/mailFolders/Inbox/messages/delta?$deltatoken=%20", GraphDeltaCursorValidationReason.MissingOpaqueToken)]
        [InlineData("https://graph.microsoft.com/v1.0/users/mailbox%40odmon.example/mailFolders/Inbox/messages/delta?return=$deltatoken=opaque", GraphDeltaCursorValidationReason.MissingOpaqueToken)]
        [InlineData("not-an-absolute-url", GraphDeltaCursorValidationReason.MalformedUrl)]
        public void GraphDeltaCursorRejectsUnsafeOrMalformedScope(
            string cursor,
            GraphDeltaCursorValidationReason expectedReason)
        {
            var exception = Assert.Throws<InvalidDataException>(() =>
                MicrosoftGraphEmailAutomationClient.ValidateDeltaCursorUrl(
                    cursor,
                    new Uri("https://graph.microsoft.com/v1.0/"),
                    "mailbox@odmon.example",
                    "Inbox"));

            Assert.True(
                MicrosoftGraphEmailAutomationClient.TryGetCursorValidationReason(
                    exception,
                    out var actualReason));
            Assert.Equal(expectedReason, actualReason);
            Assert.Equal("Microsoft Graph delta cursor validation failed.", exception.Message);
            Assert.DoesNotContain("opaque", exception.Message, StringComparison.Ordinal);
        }

        [Fact]
        public async Task GraphContentReaderRejectsOversizedPayload()
        {
            using var content = new ByteArrayContent([1, 2, 3, 4, 5]);

            await Assert.ThrowsAsync<InvalidDataException>(() =>
                MicrosoftGraphEmailAutomationClient.ReadBoundedContentAsync(
                    content,
                    maximumBytes: 4,
                    contentKind: "synthetic payload",
                    CancellationToken.None));
        }

        [Fact]
        public void DedupModelEnforcesDurableIdentityConcurrencyAndPrivacy()
        {
            using var db = CreateDb();
            var entity = db.Model.FindEntityType(typeof(EmailFilingDedup));

            Assert.NotNull(entity);
            Assert.Contains(
                entity.GetIndexes(),
                index => index.IsUnique &&
                    index.Properties.Select(property => property.Name).SequenceEqual(
                        [nameof(EmailFilingDedup.MessageFingerprint), nameof(EmailFilingDedup.TikCounter)]));
            Assert.True(entity.FindProperty(nameof(EmailFilingDedup.RowVersion))!.IsConcurrencyToken);
            Assert.Null(entity.FindProperty("OdcanitDestPath"));

            var candidate = db.Model.FindEntityType(typeof(EmailFilingCandidateDiagnostic));
            Assert.Equal(64, candidate!.FindProperty(nameof(EmailFilingCandidateDiagnostic.Candidate))!.GetMaxLength());
        }

        [Fact]
        public void OdcanitDestinationMustBeMsgUnderConfiguredProtectedRoot()
        {
            var root = Path.Combine(Path.GetTempPath(), "odmon-protected-documents");
            var valid = Path.Combine(root, "case", "document.msg");
            var traversal = Path.Combine(root, "..", "outside", "document.msg");
            var wrongExtension = Path.Combine(root, "case", "document.pdf");

            Assert.True(EmailFilingDocumentWriter.IsAllowedDestinationPath(valid, [root]));
            Assert.False(EmailFilingDocumentWriter.IsAllowedDestinationPath(traversal, [root]));
            Assert.False(EmailFilingDocumentWriter.IsAllowedDestinationPath(wrongExtension, [root]));
            Assert.False(EmailFilingDocumentWriter.IsAllowedDestinationPath(valid, []));
        }

        [Fact]
        public void EmailFilingPathLookupReusesVerifiedProtectedPathProcedureContract()
        {
            Assert.Equal(
                SqlNetCourtDocumentFileResolver.BuildDocPathCommandText,
                OdcanitDocumentWriter.BuildDocPathCommandText);
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
        public async Task MultiTikEmailIsBlockedBeforeTargetCreation()
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
            Assert.Empty(diagnostic!.Targets);
            Assert.Contains("REAL_WRITE_AUTHORITY_BLOCKED_MULTI_TIK", diagnostic.ObserverClassifications);
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
        public async Task CanonicalEvidenceExtractionAloneNeverCreatesFilingTarget()
        {
            await using var db = CreateDb();
            var graph = new FakeFilingGraphClient();
            var msgGenerator = new FakeMsgGenerator();
            var documentWriter = new FakeDocumentWriter();
            var service = CreateService(
                db,
                new Dictionary<string, int>(),
                graphClient: graph,
                msgGenerator: msgGenerator,
                documentWriter: documentWriter);

            var diagnostic = await service.ProcessAsync(
                "mailbox@odmon.example",
                [],
                Message(
                    "מספר תביעה: CLM-12345",
                    "מספר רכב: 12-345-67; תאריך אירוע: 30/08/2026"),
                CancellationToken.None);

            Assert.NotNull(diagnostic);
            Assert.Empty(diagnostic!.Targets);
            Assert.Equal(EmailFilingConstants.NoTikCandidates, diagnostic.FinalDecision);
            Assert.Equal(0, graph.MimeFetchCount);
            Assert.Equal(0, msgGenerator.GenerateCount);
            Assert.Empty(documentWriter.CreatedTikCounters);
            Assert.Empty(documentWriter.WrittenTikCounters);
        }

        [Fact]
        public async Task UniqueClaimObserverCreatesNoTargetMimeOrWrite()
        {
            await AssertPrimaryObserverOnlyAsync(
                "מספר תביעה: CLM-UNIQUE",
                new EmailCaseResolutionSnapshot(
                    [],
                    [new(EmailEvidenceType.ClaimNumber, "CLM-UNIQUE", [501])],
                    []),
                "CLAIM_UNIQUE");
        }

        [Fact]
        public async Task UniqueCourtObserverCreatesNoTargetMimeOrWrite()
        {
            await AssertPrimaryObserverOnlyAsync(
                "מספר תיק בית משפט: 12345-01-26",
                new EmailCaseResolutionSnapshot(
                    [],
                    [],
                    [new(EmailEvidenceType.CourtCaseNumber, "12345-01-26", [502])]),
                "COURT_UNIQUE");
        }

        [Fact]
        public async Task AmbiguousClaimObserverCreatesNoTargetMimeOrWrite()
        {
            await AssertPrimaryObserverOnlyAsync(
                "מספר תביעה: CLM-AMBIGUOUS",
                new EmailCaseResolutionSnapshot(
                    [],
                    [new(EmailEvidenceType.ClaimNumber, "CLM-AMBIGUOUS", [501, 502])],
                    []),
                "CLAIM_AMBIGUOUS");
        }

        [Fact]
        public async Task PrimaryConflictBlocksCurrentTikAuthority()
        {
            await using var db = CreateDb();
            var snapshot = new EmailCaseResolutionSnapshot(
                [new(EmailEvidenceType.InternalTikNumber, "9/1984", [40514])],
                [new(EmailEvidenceType.ClaimNumber, "CLM-CONFLICT", [99999])],
                []);
            var service = CreateService(
                db,
                new Dictionary<string, int> { ["9/1984"] = 40514 },
                primaryResolver: new FakePrimaryResolver(snapshot));

            var diagnostic = await service.ProcessAsync(
                "mailbox@odmon.example",
                [],
                Message("9/1984 מספר תביעה: CLM-CONFLICT"),
                CancellationToken.None);

            Assert.NotNull(diagnostic);
            Assert.Contains("PRIMARY_CONFLICT", diagnostic!.ObserverClassifications);
            Assert.Empty(diagnostic.Targets);
            Assert.Contains("REAL_WRITE_AUTHORITY_BLOCKED_CONFLICT", diagnostic.ObserverClassifications);
        }

        [Fact]
        public async Task SupportingObserverNarrowingCreatesNoTargetMimeOrWrite()
        {
            await using var db = CreateDb();
            var graph = new FakeFilingGraphClient();
            var msgGenerator = new FakeMsgGenerator();
            var documentWriter = new FakeDocumentWriter();
            var snapshot = new EmailCaseResolutionSnapshot(
                [],
                [new(EmailEvidenceType.ClaimNumber, "CLM-AMBIGUOUS", [501, 502])],
                []);
            var repository = new FakeSupportingResolutionRepository(new HashSet<int> { 502 });
            var service = CreateService(
                db,
                new Dictionary<string, int>(),
                dryRun: false,
                realWriteEnabled: true,
                primaryResolver: new FakePrimaryResolver(snapshot),
                resolutionEngine: new EmailCaseResolutionEngine(repository),
                graphClient: graph,
                msgGenerator: msgGenerator,
                documentWriter: documentWriter);

            var diagnostic = await service.ProcessAsync(
                "mailbox@odmon.example",
                [],
                Message("מספר תביעה: CLM-AMBIGUOUS מספר רכב: 22-222-22"),
                CancellationToken.None);

            Assert.NotNull(diagnostic);
            Assert.Contains("NARROWED_BY_VEHICLE", diagnostic!.ObserverClassifications);
            Assert.Empty(diagnostic.Targets);
            Assert.Equal(1, repository.SupportingCallCount);
            Assert.Equal(0, graph.MimeFetchCount);
            Assert.Equal(0, msgGenerator.GenerateCount);
            Assert.Empty(documentWriter.CreatedTikCounters);
            Assert.Empty(documentWriter.WrittenTikCounters);
        }

        [Fact]
        public async Task SupportingEvidenceAloneNeverQueriesOdcanitOrCreatesTarget()
        {
            await using var db = CreateDb();
            var graph = new FakeFilingGraphClient();
            var documentWriter = new FakeDocumentWriter();
            var repository = new FakeSupportingResolutionRepository(new HashSet<int> { 502 });
            var service = CreateService(
                db,
                new Dictionary<string, int>(),
                dryRun: false,
                realWriteEnabled: true,
                primaryResolver: new FakePrimaryResolver(EmailCaseResolutionSnapshot.Empty),
                resolutionEngine: new EmailCaseResolutionEngine(repository),
                graphClient: graph,
                documentWriter: documentWriter);

            var diagnostic = await service.ProcessAsync(
                "mailbox@odmon.example",
                [],
                Message("מספר רכב: 22-222-22"),
                CancellationToken.None);

            Assert.NotNull(diagnostic);
            Assert.Contains("SUPPORTING_EVIDENCE_NO_EFFECT", diagnostic!.ObserverClassifications);
            Assert.Equal(0, repository.SupportingCallCount);
            Assert.Empty(diagnostic.Targets);
            Assert.Equal(0, graph.MimeFetchCount);
            Assert.Empty(documentWriter.CreatedTikCounters);
            Assert.Empty(documentWriter.WrittenTikCounters);
        }

        [Fact]
        public async Task SupportingConflictBlocksCurrentTikTarget()
        {
            await using var db = CreateDb();
            var snapshot = new EmailCaseResolutionSnapshot(
                [new(EmailEvidenceType.InternalTikNumber, "9/1984", [40514])],
                [],
                []);
            var service = CreateService(
                db,
                new Dictionary<string, int> { ["9/1984"] = 40514 },
                primaryResolver: new FakePrimaryResolver(snapshot),
                resolutionEngine: new EmailCaseResolutionEngine(
                    new FakeSupportingResolutionRepository(new HashSet<int>())));

            var diagnostic = await service.ProcessAsync(
                "mailbox@odmon.example",
                [],
                Message("9/1984 מספר רכב: 22-222-22"),
                CancellationToken.None);

            Assert.NotNull(diagnostic);
            Assert.Contains("SUPPORTING_EVIDENCE_CONFLICT", diagnostic!.ObserverClassifications);
            Assert.Empty(diagnostic.Targets);
            Assert.Contains("REAL_WRITE_AUTHORITY_BLOCKED_CONFLICT", diagnostic.ObserverClassifications);
            Assert.NotNull(diagnostic.ResolutionRun);
            Assert.Single(db.EmailFilingResolutionRuns);
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public async Task DirectInsuranceUniqueClaimOverridesUnrelatedFreeTextTik(
            bool resolutionPhantomEnabled)
        {
            await using var db = CreateDb();
            var writer = new FakeDocumentWriter();
            var snapshot = new EmailCaseResolutionSnapshot(
                [],
                [new(EmailEvidenceType.ClaimNumber, "SYNTHETIC-CLAIM-A", [501])],
                []);
            var service = CreateService(
                db,
                new Dictionary<string, int>
                {
                    ["1/100"] = 501,
                    ["2/200"] = 502
                },
                dryRun: false,
                realWriteEnabled: true,
                allowAllResolvedTikNumbers: true,
                resolutionPhantomEnabled: resolutionPhantomEnabled,
                primaryResolver: new FakePrimaryResolver(snapshot),
                documentWriter: writer);

            var diagnostic = await service.ProcessAsync(
                "mailbox@odmon.example",
                [],
                Message(
                    "FW: synthetic direct template",
                    DirectInsuranceBody("SYNTHETIC-CLAIM-A", "2/200")),
                CancellationToken.None);

            var target = Assert.Single(diagnostic!.Targets);
            Assert.Equal(501, target.TikCounter);
            Assert.DoesNotContain(diagnostic.Targets, item => item.TikCounter == 502);
            Assert.Contains("REAL_WRITE_AUTHORITY_DIRECT_UNIQUE_CLAIM", diagnostic.ObserverClassifications);
            Assert.Equal([501], writer.WrittenTikCounters);
            if (resolutionPhantomEnabled)
            {
                var run = Assert.IsType<EmailFilingResolutionRun>(diagnostic.ResolutionRun);
                Assert.Equal(
                    EmailFilingAuthorityDecisionClasses.AllowedDirectUniqueClaim,
                    run.AuthorityDecisionClass);
                Assert.Equal(EmailFilingSourceTemplateKinds.DirectInsurance, run.SourceTemplateKind);
                Assert.True(run.PreferredClaimUsed);
            }
        }

        [Fact]
        public async Task DirectInsuranceAmbiguousClaimCanBeNarrowedByVehicle()
        {
            await using var db = CreateDb();
            var writer = new FakeDocumentWriter();
            var snapshot = new EmailCaseResolutionSnapshot(
                [],
                [new(EmailEvidenceType.ClaimNumber, "SYNTHETIC-CLAIM-A", [501, 502])],
                []);
            var service = CreateService(
                db,
                new Dictionary<string, int>
                {
                    ["1/100"] = 501,
                    ["2/200"] = 502
                },
                dryRun: false,
                realWriteEnabled: true,
                allowAllResolvedTikNumbers: true,
                primaryResolver: new FakePrimaryResolver(snapshot),
                resolutionEngine: new EmailCaseResolutionEngine(
                    new FakeSupportingResolutionRepository(new HashSet<int> { 502 })),
                documentWriter: writer);

            var diagnostic = await service.ProcessAsync(
                "mailbox@odmon.example",
                [],
                Message(
                    "synthetic direct template",
                    DirectInsuranceBody("SYNTHETIC-CLAIM-A")),
                CancellationToken.None);

            var target = Assert.Single(diagnostic!.Targets);
            Assert.Equal(502, target.TikCounter);
            Assert.Contains("NARROWED_BY_VEHICLE", diagnostic.ObserverClassifications);
            Assert.Contains("REAL_WRITE_AUTHORITY_DIRECT_CLAIM_VEHICLE", diagnostic.ObserverClassifications);
            Assert.Equal([502], writer.WrittenTikCounters);
            var run = Assert.IsType<EmailFilingResolutionRun>(diagnostic.ResolutionRun);
            Assert.Equal(
                EmailFilingAuthorityDecisionClasses.AllowedDirectClaimVehicle,
                run.AuthorityDecisionClass);
            Assert.Equal(1, run.DecisiveSupportingEvidenceTypeMask);
            Assert.Contains(run.Candidates, candidate =>
                candidate.TikCounter == 501 &&
                candidate.PrimaryEvidenceType == EmailFilingResolutionPrimaryEvidenceTypes.Claim &&
                candidate.CandidateStage == EmailFilingResolutionCandidateStages.PrimaryCandidate);
            Assert.Contains(run.Candidates, candidate =>
                candidate.TikCounter == 502 &&
                candidate.PrimaryEvidenceType == EmailFilingResolutionPrimaryEvidenceTypes.Claim &&
                candidate.CandidateStage == EmailFilingResolutionCandidateStages.AfterSupport);
        }

        [Fact]
        public async Task UniqueExactCourtCaseNumberIsAuthorizedForRealWrite()
        {
            await using var db = CreateDb();
            var writer = new FakeDocumentWriter();
            var snapshot = new EmailCaseResolutionSnapshot(
                [],
                [],
                [new(EmailEvidenceType.CourtCaseNumber, "12345-01-26", [501])]);
            var service = CreateService(
                db,
                new Dictionary<string, int> { ["1/100"] = 501 },
                dryRun: false,
                realWriteEnabled: true,
                allowAllResolvedTikNumbers: true,
                primaryResolver: new FakePrimaryResolver(snapshot),
                documentWriter: writer);

            var diagnostic = await service.ProcessAsync(
                "mailbox@odmon.example",
                [],
                Message("מספר תיק בית משפט: 12345-01-26"),
                CancellationToken.None);

            Assert.Equal(EmailFilingConstants.Filed, diagnostic!.FinalDecision);
            Assert.Equal(501, Assert.Single(diagnostic.Targets).TikCounter);
            Assert.Equal([501], writer.WrittenTikCounters);
            Assert.Contains("REAL_WRITE_AUTHORITY_EXACT_COURT", diagnostic.ObserverClassifications);
            Assert.Equal(
                EmailFilingAuthorityDecisionClasses.AllowedExactCourt,
                Assert.IsType<EmailFilingResolutionRun>(diagnostic.ResolutionRun)
                    .AuthorityDecisionClass);
        }

        [Fact]
        public async Task DirectInsuranceWithoutUsablePreferredClaimFallsBackToGenericTik()
        {
            await using var db = CreateDb();
            var service = CreateService(
                db,
                new Dictionary<string, int> { ["2/200"] = 502 });
            var body =
                "פרטי צד ג : תביעה - SYNTHETIC-X\r\n" +
                "מספר רכב - 12-345-67\r\n" +
                "תיק תביעה מספר : ---\r\n" +
                "תאריך אירוע : 30/08/2026\r\n" +
                "free text 2/200";

            var diagnostic = await service.ProcessAsync(
                "mailbox@odmon.example",
                [],
                Message("synthetic fallback", body),
                CancellationToken.None);

            var target = Assert.Single(diagnostic!.Targets);
            Assert.Equal(502, target.TikCounter);
            Assert.Contains("REAL_WRITE_AUTHORITY_EXACT_TIK", diagnostic.ObserverClassifications);
        }

        [Fact]
        public async Task GenericMultiTikIsBlockedOutsideDirectInsurance()
        {
            await using var db = CreateDb();
            var service = CreateService(
                db,
                new Dictionary<string, int>
                {
                    ["1/100"] = 501,
                    ["2/200"] = 502
                });

            var diagnostic = await service.ProcessAsync(
                "mailbox@odmon.example",
                [],
                Message("generic 1/100 and 2/200"),
                CancellationToken.None);

            Assert.Empty(diagnostic!.Targets);
            Assert.Contains("REAL_WRITE_AUTHORITY_BLOCKED_MULTI_TIK", diagnostic.ObserverClassifications);
        }

        [Fact]
        public async Task ResolutionPhantomDisabledStillRunsAuthorityResolverWithoutPersistingPhantomRun()
        {
            await using var db = CreateDb();
            var primaryResolver = new FakePrimaryResolver(
                resolutions: new Dictionary<string, int> { ["9/1984"] = 40514 });
            var service = CreateService(
                db,
                new Dictionary<string, int> { ["9/1984"] = 40514 },
                resolutionPhantomEnabled: false,
                primaryResolver: primaryResolver);

            var diagnostic = await service.ProcessAsync(
                "mailbox@odmon.example",
                [],
                Message("9/1984"),
                CancellationToken.None);

            Assert.False(new EmailFilingSettings().ResolutionPhantomEnabled);
            Assert.Equal(1, primaryResolver.CallCount);
            Assert.Null(diagnostic!.ResolutionRun);
            Assert.Empty(db.EmailFilingResolutionRuns);
            Assert.Single(diagnostic.Targets);
        }

        [Fact]
        public async Task PhantomUniqueResultPersistsTelemetryButCreatesNoProductionWork()
        {
            await using var db = CreateDb();
            var graph = new FakeFilingGraphClient();
            var generator = new FakeMsgGenerator();
            var writer = new FakeDocumentWriter();
            var snapshot = new EmailCaseResolutionSnapshot(
                [],
                [new(EmailEvidenceType.ClaimNumber, "SYNTHETIC-CLAIM", [501])],
                []);
            var service = CreateService(
                db,
                new Dictionary<string, int>(),
                dryRun: false,
                realWriteEnabled: true,
                primaryResolver: new FakePrimaryResolver(snapshot),
                graphClient: graph,
                msgGenerator: generator,
                documentWriter: writer);

            var diagnostic = await service.ProcessAsync(
                "mailbox@odmon.example",
                [],
                Message("מספר תביעה: SYNTHETIC-CLAIM"),
                CancellationToken.None);

            Assert.Empty(diagnostic!.Targets);
            Assert.Equal(EmailFilingConstants.NoTikCandidates, diagnostic.FinalDecision);
            var run = Assert.IsType<EmailFilingResolutionRun>(diagnostic.ResolutionRun);
            Assert.Equal(EmailFilingResolutionClasses.PrimaryUnique, run.FinalResolutionClass);
            Assert.Equal(1, run.PhantomTargetCount);
            Assert.Equal(EmailFilingResolutionAgreements.NoExistingTikAuthority,
                run.AgreementWithExistingAuthority);
            Assert.Equal(501, Assert.Single(run.Targets).TikCounter);
            Assert.Equal(0, graph.MimeFetchCount);
            Assert.Equal(0, generator.GenerateCount);
            Assert.Empty(writer.CreatedTikCounters);
            Assert.Empty(writer.WrittenTikCounters);
            Assert.Empty(db.EmailFilingDedups);
        }

        [Fact]
        public async Task PhantomMultipleWouldFileCountersNeverBecomeProductionTargets()
        {
            await using var db = CreateDb();
            var graph = new FakeFilingGraphClient();
            var generator = new FakeMsgGenerator();
            var writer = new FakeDocumentWriter();
            var first = new EvidenceResolutionResult(
                EmailEvidenceType.InternalTikNumber, "1/111", [501]);
            var second = new EvidenceResolutionResult(
                EmailEvidenceType.InternalTikNumber, "2/222", [502]);
            var snapshot = new EmailCaseResolutionSnapshot([first, second], [], []);
            var service = CreateService(
                db,
                new Dictionary<string, int>(),
                dryRun: false,
                realWriteEnabled: true,
                primaryResolver: new FakePrimaryResolver(snapshot),
                graphClient: graph,
                msgGenerator: generator,
                documentWriter: writer);

            var diagnostic = await service.ProcessAsync(
                "mailbox@odmon.example",
                [],
                Message("1/111 2/222"),
                CancellationToken.None);

            Assert.Empty(diagnostic!.Targets);
            var run = Assert.IsType<EmailFilingResolutionRun>(diagnostic.ResolutionRun);
            Assert.Equal(2, run.PhantomTargetCount);
            Assert.Equal(EmailFilingResolutionClasses.MultiTik, run.FinalResolutionClass);
            Assert.All(run.Targets, target => Assert.Equal(
                EmailFilingResolutionTargetKinds.PhantomWouldFile,
                target.TargetKind));
            Assert.Equal(0, graph.MimeFetchCount);
            Assert.Equal(0, generator.GenerateCount);
            Assert.Empty(writer.CreatedTikCounters);
            Assert.Empty(writer.WrittenTikCounters);
            Assert.Empty(db.EmailFilingDedups);
        }

        [Fact]
        public async Task ResolverExceptionIsRecordedSafelyAndBlocksRealWrite()
        {
            await using var db = CreateDb();
            var graph = new FakeFilingGraphClient();
            var writer = new FakeDocumentWriter();
            var service = CreateService(
                db,
                new Dictionary<string, int> { ["9/1984"] = 40514 },
                dryRun: false,
                realWriteEnabled: true,
                primaryResolver: new ThrowingPrimaryResolver(),
                graphClient: graph,
                documentWriter: writer);

            var diagnostic = await service.ProcessAsync(
                "mailbox@odmon.example",
                [],
                Message("9/1984"),
                CancellationToken.None);

            Assert.Equal(EmailFilingConstants.NoValidTik, diagnostic!.FinalDecision);
            Assert.Equal(0, graph.MimeFetchCount);
            Assert.Empty(writer.WrittenTikCounters);
            Assert.Empty(diagnostic.Targets);
            Assert.Contains(
                "REAL_WRITE_AUTHORITY_BLOCKED_RESOLVER_ERROR",
                diagnostic.ObserverClassifications);
            var run = Assert.IsType<EmailFilingResolutionRun>(diagnostic.ResolutionRun);
            Assert.Equal(EmailFilingResolutionClasses.ObserverError, run.FinalResolutionClass);
            Assert.Equal(nameof(InvalidOperationException), run.ObserverErrorCategory);
            Assert.Equal(
                EmailFilingAuthorityDecisionClasses.BlockedResolverError,
                run.AuthorityDecisionClass);
            Assert.Equal(EmailFilingResolutionAgreements.NoExistingTikAuthority,
                run.AgreementWithExistingAuthority);
            Assert.DoesNotContain("SYNTHETIC-PRIVATE-VALUE", run.ObserverErrorCategory);
        }

        [Fact]
        public void AllowAllResolvedTikNumbersDefaultsFalse()
        {
            Assert.False(new EmailFilingSettings().AllowAllResolvedTikNumbers);
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
            Assert.Equal(
                EmailFilingAuthorityDecisionClasses.AllowedExactTik,
                Assert.IsType<EmailFilingResolutionRun>(diagnostic.ResolutionRun)
                    .AuthorityDecisionClass);
        }

        [Fact]
        public async Task DirectSourceTelemetryDoesNotChangeGenericExactTikAuthority()
        {
            await using var db = CreateDb();
            var service = CreateService(
                db,
                new Dictionary<string, int> { ["9/1984"] = 40514 });

            var diagnostic = await service.ProcessAsync(
                "mailbox@odmon.example",
                [],
                Message(
                    "9/1984",
                    sender: "unit.test@5555555.co.il"),
                CancellationToken.None);

            var run = Assert.IsType<EmailFilingResolutionRun>(diagnostic!.ResolutionRun);
            Assert.Equal(
                EmailFilingAuthorityDecisionClasses.AllowedExactTik,
                run.AuthorityDecisionClass);
            Assert.Equal(EmailFilingSourceTemplateKinds.DirectInsurance, run.SourceTemplateKind);
            Assert.False(run.PreferredClaimUsed);
            Assert.Single(diagnostic.Targets);
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
        public async Task TwoAllowlistedTikCasesAreBlockedAsMultiTik()
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
            Assert.Equal(EmailFilingConstants.NoValidTik, diagnostic!.FinalDecision);
            Assert.Empty(diagnostic.Targets);
            Assert.Contains("REAL_WRITE_AUTHORITY_BLOCKED_MULTI_TIK", diagnostic.ObserverClassifications);
            Assert.Equal(0, graph.MimeFetchCount);
            Assert.Equal(0, generator.GenerateCount);
            Assert.Empty(writer.WrittenTikCounters);
            Assert.Empty(db.EmailFilingDedups);
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
        public async Task MultiTikIsBlockedBeforeExistingDedupStateIsConsidered()
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

            Assert.Equal(EmailFilingConstants.NoValidTik, diagnostic!.FinalDecision);
            Assert.Empty(diagnostic.Targets);
            Assert.Empty(writer.WrittenTikCounters);
            Assert.Equal(0, graph.MimeFetchCount);
            Assert.Equal(0, generator.GenerateCount);
            Assert.Single(db.EmailFilingDedups);
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
        public async Task MultiTikIsBlockedBeforeAnyTargetCanFail()
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

            var diagnostic = await service.ProcessAsync(
                "mailbox@odmon.example", [], Message("9/1984 and 253/248"), CancellationToken.None);

            Assert.Equal(EmailFilingConstants.NoValidTik, diagnostic!.FinalDecision);
            Assert.Empty(diagnostic.Targets);
            Assert.Empty(db.EmailFilingDedups);
            Assert.Equal(0, graph.MimeFetchCount);
            Assert.Equal(0, generator.GenerateCount);
            Assert.Empty(writer.WrittenTikCounters);
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
        public async Task CorrectTikNumberWithWrongCounterFailsClosed()
        {
            await AssertRealWriteGateClosedAsync(
                "9/1984",
                new Dictionary<string, int> { ["9/1984"] = 99999 },
                [new EmailFilingAllowlistEntry { TikNumber = "9/1984", TikCounter = 40514 }]);
        }

        [Fact]
        public async Task WrongTikNumberWithCorrectCounterFailsClosed()
        {
            await AssertRealWriteGateClosedAsync(
                "8/1984",
                new Dictionary<string, int> { ["8/1984"] = 40514 },
                [new EmailFilingAllowlistEntry { TikNumber = "9/1984", TikCounter = 40514 }]);
        }

        [Fact]
        public async Task EmptyRealWriteAllowlistFailsClosed()
        {
            await AssertRealWriteGateClosedAsync(
                "9/1984",
                new Dictionary<string, int> { ["9/1984"] = 40514 },
                []);
        }

        [Fact]
        public async Task ExplicitUnrestrictedModeWritesUniqueResolutionWithoutAllowlist()
        {
            await using var db = CreateDb();
            var writer = new FakeDocumentWriter();
            var service = CreateService(
                db,
                new Dictionary<string, int> { ["253/248"] = 60002 },
                dryRun: false,
                realWriteEnabled: true,
                allowAllResolvedTikNumbers: true,
                allowlist: [],
                documentWriter: writer);

            var diagnostic = await service.ProcessAsync(
                "mailbox@odmon.example", [], Message("253/248"), CancellationToken.None);

            Assert.Equal(EmailFilingConstants.Filed, diagnostic!.FinalDecision);
            Assert.Equal([60002], writer.WrittenTikCounters);
            Assert.False(Assert.Single(diagnostic.Targets).RealWriteAllowlisted);
        }

        [Fact]
        public async Task UnrestrictedModeWithMalformedAllowlistFailsClosed()
        {
            await using var db = CreateDb();
            var graph = new FakeFilingGraphClient();
            var writer = new FakeDocumentWriter();
            var service = CreateService(
                db,
                new Dictionary<string, int> { ["9/1984"] = 40514 },
                dryRun: false,
                realWriteEnabled: true,
                allowAllResolvedTikNumbers: true,
                allowlist:
                [
                    new EmailFilingAllowlistEntry { TikNumber = "malformed", TikCounter = 40514 }
                ],
                graphClient: graph,
                documentWriter: writer);

            var diagnostic = await service.ProcessAsync(
                "mailbox@odmon.example", [], Message("9/1984"), CancellationToken.None);

            Assert.Equal(EmailFilingConstants.NotAllowlisted, diagnostic!.FinalDecision);
            Assert.Equal(0, graph.MimeFetchCount);
            Assert.Empty(writer.WrittenTikCounters);
        }

        [Fact]
        public async Task UnrestrictedModeNeverAuthorizesUnresolvedAmbiguousOrCourtOnlyCandidates()
        {
            await using var db = CreateDb();
            var writer = new FakeDocumentWriter();
            var courtResolver = new FakeCourtResolver(new Dictionary<string, EmailAutomationCaseMatch>
            {
                ["123-45-67"] = new EmailAutomationCaseMatch(70003, "700/3", null)
            });
            var service = CreateService(
                db,
                new Dictionary<string, int>(),
                dryRun: false,
                realWriteEnabled: true,
                allowAllResolvedTikNumbers: true,
                allowlist: [],
                ambiguousTikNumbers: new Dictionary<string, IReadOnlyList<int>>
                {
                    ["9/1984"] = [40514, 60002]
                },
                courtResolver: courtResolver,
                documentWriter: writer);
            var rules = new[]
            {
                new EmailAutomationRuleSettings
                {
                    Enabled = true,
                    SubjectRegex = @"\d{3}-\d{2}-\d{2}"
                }
            };

            var diagnostic = await service.ProcessAsync(
                "mailbox@odmon.example",
                rules,
                Message("9/1984 253/248 Court 123-45-67"),
                CancellationToken.None);

            Assert.Equal(EmailFilingConstants.NoValidTik, diagnostic!.FinalDecision);
            Assert.Empty(diagnostic.Targets);
            Assert.Empty(writer.WrittenTikCounters);
            Assert.Contains(diagnostic.Candidates, candidate =>
                candidate.Candidate == "9/1984" &&
                candidate.ResolutionStatus == EmailFilingConstants.TikAmbiguous);
            Assert.Contains("COURT_ONLY", diagnostic.ObserverClassifications);
        }

        [Fact]
        public async Task UnrestrictedMultiTikIncludingAmbiguousValueIsBlocked()
        {
            await using var db = CreateDb();
            var writer = new FakeDocumentWriter();
            var graph = new FakeFilingGraphClient();
            var generator = new FakeMsgGenerator();
            var service = CreateService(
                db,
                new Dictionary<string, int>
                {
                    ["9/1984"] = 40514,
                    ["253/248"] = 60002
                },
                dryRun: false,
                realWriteEnabled: true,
                allowAllResolvedTikNumbers: true,
                allowlist: [],
                ambiguousTikNumbers: new Dictionary<string, IReadOnlyList<int>>
                {
                    ["7/1236002"] = [70003, 70004]
                },
                graphClient: graph,
                msgGenerator: generator,
                documentWriter: writer);

            var diagnostic = await service.ProcessAsync(
                "mailbox@odmon.example",
                [],
                Message("9/1984 and 253/248 and 7/1236002", "Duplicate 9/1984"),
                CancellationToken.None);

            Assert.Equal(EmailFilingConstants.NoValidTik, diagnostic!.FinalDecision);
            Assert.Empty(writer.WrittenTikCounters);
            Assert.Empty(diagnostic.Targets);
            Assert.Equal(0, graph.MimeFetchCount);
            Assert.Equal(0, generator.GenerateCount);
            Assert.Contains(diagnostic.Candidates, candidate =>
                candidate.Candidate == "7/1236002" &&
                candidate.ResolutionStatus == EmailFilingConstants.TikAmbiguous);
        }

        [Fact]
        public async Task UnrestrictedModeBlocksEmailContainingAmbiguousTik()
        {
            await using var db = CreateDb();
            var writer = new FakeDocumentWriter();
            var service = CreateService(
                db,
                new Dictionary<string, int> { ["9/1984"] = 40514 },
                dryRun: false,
                realWriteEnabled: true,
                allowAllResolvedTikNumbers: true,
                allowlist: [],
                ambiguousTikNumbers: new Dictionary<string, IReadOnlyList<int>>
                {
                    ["253/248"] = [60002, 60003]
                },
                documentWriter: writer);

            var diagnostic = await service.ProcessAsync(
                "mailbox@odmon.example",
                [],
                Message("9/1984 and 253/248"),
                CancellationToken.None);

            Assert.Equal(EmailFilingConstants.NoValidTik, diagnostic!.FinalDecision);
            Assert.Empty(writer.WrittenTikCounters);
            Assert.Empty(diagnostic.Targets);
            Assert.Contains(diagnostic.Candidates, candidate =>
                candidate.Candidate == "253/248" &&
                candidate.ResolutionStatus == EmailFilingConstants.TikAmbiguous);
        }

        [Fact]
        public async Task MalformedOrInconsistentRealWriteAllowlistFailsClosed()
        {
            await AssertRealWriteGateClosedAsync(
                "9/1984",
                new Dictionary<string, int> { ["9/1984"] = 40514 },
                [
                    new EmailFilingAllowlistEntry { TikNumber = "9/1984", TikCounter = 40514 },
                    new EmailFilingAllowlistEntry { TikNumber = "9/1984", TikCounter = 60002 }
                ]);
        }

        [Fact]
        public async Task DryRunStillPreventsWriteWhenRealWriteEnabled()
        {
            await using var db = CreateDb();
            var graph = new FakeFilingGraphClient();
            var writer = new FakeDocumentWriter();
            var service = CreateService(
                db,
                new Dictionary<string, int> { ["9/1984"] = 40514 },
                dryRun: true,
                realWriteEnabled: true,
                graphClient: graph,
                documentWriter: writer);

            var diagnostic = await service.ProcessAsync(
                "mailbox@odmon.example", [], Message("9/1984"), CancellationToken.None);

            Assert.Equal(EmailFilingConstants.DryRunWouldFile, diagnostic!.FinalDecision);
            Assert.Equal(0, graph.MimeFetchCount);
            Assert.Empty(writer.CreatedTikCounters);
            Assert.Empty(writer.WrittenTikCounters);
            Assert.Empty(db.EmailFilingDedups);
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
        public void ObserverClassificationFormattingCannotOverflowPersistedColumn()
        {
            var formatted = EmailFilingService.FormatObserverClassifications(
                Enumerable.Range(1, 30)
                    .Select(index => $"SYNTHETIC_CLASSIFICATION_{index:D2}"));

            Assert.True(formatted.Length <= 256);
            Assert.Contains("OBSERVER_TRUNCATED", formatted);
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
                AllowHistoricalBackfill = true,
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
                new EmailCaseEvidenceExtractor(),
                new FakePrimaryResolver(
                    resolutions: new Dictionary<string, int> { ["9/1984"] = 40514 }),
                new FakeResolutionEngine(),
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

        [Fact]
        public async Task BrandNewMailboxStartsAtInitializationTimeNotHistory()
        {
            await using var db = CreateDb();
            var now = ReceivedUtc.AddMinutes(5);
            var graph = new FakeFilingGraphClient();
            var polling = CreatePollingService(
                db,
                graph,
                new EmailFilingSettings { Enabled = true, DryRun = true },
                now);

            await polling.RunAsync(CancellationToken.None);

            var state = Assert.Single(db.EmailFilingMailboxStates);
            Assert.Equal(now, state.ProcessingFromUtc);
            Assert.Equal(now, graph.LastProcessingFromUtc);
            Assert.Null(graph.LastRequestedDeltaLink);
        }

        [Fact]
        public async Task ExistingCursorResumesWithoutReinitializingBaseline()
        {
            await using var db = CreateDb();
            var baseline = ReceivedUtc.AddDays(-10);
            db.EmailFilingMailboxStates.Add(new EmailFilingMailboxState
            {
                Mailbox = "mailbox@odmon.example",
                FolderId = "Inbox",
                DeltaLink = "existing-cursor",
                ProcessingFromUtc = baseline,
                CreatedAtUtc = baseline,
                UpdatedAtUtc = baseline
            });
            await db.SaveChangesAsync();
            var graph = new FakeFilingGraphClient { ReturnedDeltaLink = "next-cursor" };
            var polling = CreatePollingService(
                db,
                graph,
                new EmailFilingSettings { Enabled = true, DryRun = true },
                ReceivedUtc);

            await polling.RunAsync(CancellationToken.None);

            Assert.Equal("existing-cursor", graph.LastRequestedDeltaLink);
            var state = Assert.Single(db.EmailFilingMailboxStates);
            Assert.Equal(baseline, state.ProcessingFromUtc);
            Assert.Equal("next-cursor", state.DeltaLink);
        }

        [Fact]
        public async Task InvalidExistingCursorFailsClosedWithoutReset()
        {
            await using var db = CreateDb();
            db.EmailFilingMailboxStates.Add(new EmailFilingMailboxState
            {
                Mailbox = "mailbox@odmon.example",
                FolderId = "Inbox",
                DeltaLink = "corrupt-cursor",
                ProcessingFromUtc = ReceivedUtc.AddDays(-1),
                CreatedAtUtc = ReceivedUtc.AddDays(-1),
                UpdatedAtUtc = ReceivedUtc.AddDays(-1)
            });
            await db.SaveChangesAsync();
            var graph = new FakeFilingGraphClient
            {
                DeltaException = new InvalidDataException("Synthetic corrupt cursor.")
            };
            var polling = CreatePollingService(
                db,
                graph,
                new EmailFilingSettings { Enabled = true, DryRun = true },
                ReceivedUtc);

            await Assert.ThrowsAsync<InvalidDataException>(
                () => polling.RunAsync(CancellationToken.None));

            Assert.Equal("corrupt-cursor", graph.LastRequestedDeltaLink);
            Assert.Equal("corrupt-cursor", Assert.Single(db.EmailFilingMailboxStates).DeltaLink);
        }

        [Fact]
        public async Task ProcessingFailureDoesNotAdvanceCursorAndRestartReplays()
        {
            await using var db = CreateDb();
            var settings = new EmailFilingSettings
            {
                Enabled = true,
                DryRun = false,
                RealWriteEnabled = true,
                StartProcessingFromUtc = ReceivedUtc.AddMinutes(-1),
                AllowHistoricalBackfill = true,
                RealWriteAllowlist =
                [
                    new EmailFilingAllowlistEntry { TikNumber = "9/1984", TikCounter = 40514 }
                ]
            };
            var message = Message("9/1984");
            var firstGraph = new FakeFilingGraphClient(message);
            var failingService = CreateService(
                db,
                new Dictionary<string, int> { ["9/1984"] = 40514 },
                dryRun: false,
                realWriteEnabled: true,
                graphClient: firstGraph,
                msgGenerator: new FakeMsgGenerator { ThrowOnGenerate = true });
            var firstPolling = CreatePollingService(
                db, firstGraph, settings, ReceivedUtc.AddMinutes(1), failingService);

            await Assert.ThrowsAsync<EmailFilingProcessingException>(
                () => firstPolling.RunAsync(CancellationToken.None));
            Assert.Null(Assert.Single(db.EmailFilingMailboxStates).DeltaLink);

            var replayGraph = new FakeFilingGraphClient(message);
            var replayService = CreateService(
                db,
                new Dictionary<string, int> { ["9/1984"] = 40514 });
            var replayPolling = CreatePollingService(
                db, replayGraph, settings, ReceivedUtc.AddMinutes(2), replayService);
            await replayPolling.RunAsync(CancellationToken.None);

            Assert.Null(replayGraph.LastRequestedDeltaLink);
            Assert.Equal("filing-delta-1", Assert.Single(db.EmailFilingMailboxStates).DeltaLink);
        }

        [Fact]
        public async Task HistoricalBaselineRequiresExplicitOptIn()
        {
            await using var db = CreateDb();
            var settings = new EmailFilingSettings
            {
                Enabled = true,
                DryRun = true,
                StartProcessingFromUtc = ReceivedUtc.AddDays(-1),
                AllowHistoricalBackfill = false
            };
            var graph = new FakeFilingGraphClient();
            var polling = CreatePollingService(db, graph, settings, ReceivedUtc);

            await Assert.ThrowsAsync<InvalidOperationException>(
                () => polling.RunAsync(CancellationToken.None));

            Assert.Empty(db.EmailFilingMailboxStates);
            Assert.Equal(0, graph.DeltaFetchCount);
        }

        private static EmailFilingPollingService CreatePollingService(
            IntegrationDbContext db,
            IEmailFilingGraphClient graphClient,
            EmailFilingSettings settings,
            DateTime utcNow,
            EmailFilingService? filingService = null)
        {
            var automationSettings = new EmailAutomationSettings
            {
                FingerprintKey = "synthetic-email-filing-test-key",
                Mailboxes =
                [
                    new EmailAutomationMailboxSettings
                    {
                        Address = "mailbox@odmon.example",
                        InboxFolder = "Inbox"
                    }
                ]
            };
            return new EmailFilingPollingService(
                db,
                graphClient,
                filingService ?? CreateService(db, new Dictionary<string, int>()),
                Options.Create(settings),
                Options.Create(automationSettings),
                NullLogger<EmailFilingPollingService>.Instance,
                new FixedTimeProvider(utcNow));
        }

        private static async Task AssertRealWriteGateClosedAsync(
            string candidate,
            IReadOnlyDictionary<string, int> resolutions,
            IReadOnlyList<EmailFilingAllowlistEntry> allowlist)
        {
            await using var db = CreateDb();
            var graph = new FakeFilingGraphClient();
            var writer = new FakeDocumentWriter();
            var service = CreateService(
                db,
                resolutions,
                dryRun: false,
                realWriteEnabled: true,
                allowlist: allowlist,
                graphClient: graph,
                documentWriter: writer);

            var diagnostic = await service.ProcessAsync(
                "mailbox@odmon.example", [], Message(candidate), CancellationToken.None);

            Assert.Equal(EmailFilingConstants.NotAllowlisted, diagnostic!.FinalDecision);
            Assert.Equal(0, graph.MimeFetchCount);
            Assert.Empty(writer.CreatedTikCounters);
            Assert.Empty(writer.WrittenTikCounters);
            Assert.Empty(db.EmailFilingDedups);
        }

        private static EmailFilingService CreateService(
            IntegrationDbContext db,
            IReadOnlyDictionary<string, int> resolvedTikNumbers,
            bool dryRun = true,
            bool realWriteEnabled = false,
            bool allowAllResolvedTikNumbers = false,
            bool resolutionPhantomEnabled = true,
            IEmailAutomationCaseResolver? courtResolver = null,
            IReadOnlyList<EmailFilingAllowlistEntry>? allowlist = null,
            IReadOnlyDictionary<string, IReadOnlyList<int>>? ambiguousTikNumbers = null,
            IEmailCasePrimaryResolver? primaryResolver = null,
            IEmailCaseResolutionEngine? resolutionEngine = null,
            IEmailFilingGraphClient? graphClient = null,
            IEmailMsgGenerator? msgGenerator = null,
            IEmailFilingDocumentWriter? documentWriter = null,
            IEmailNotifier? emailNotifier = null)
        {
            var filingSettings = new EmailFilingSettings
            {
                Enabled = true,
                DryRun = dryRun,
                RealWriteEnabled = realWriteEnabled,
                AllowAllResolvedTikNumbers = allowAllResolvedTikNumbers,
                ResolutionPhantomEnabled = resolutionPhantomEnabled,
                RealWriteAllowlist = allowlist?.ToList() ??
                [
                    new EmailFilingAllowlistEntry
                    {
                        TikNumber = "9/1984",
                        TikCounter = 40514
                    }
                ]
            };
            var automationSettings = new EmailAutomationSettings
            {
                FingerprintKey = "synthetic-email-filing-test-key"
            };
            return new EmailFilingService(
                db,
                new FakeOdcanitReader(resolvedTikNumbers, ambiguousTikNumbers),
                courtResolver ?? new FakeCourtResolver(new Dictionary<string, EmailAutomationCaseMatch>()),
                new EmailCaseEvidenceExtractor(),
                primaryResolver ?? new FakePrimaryResolver(
                    resolutions: resolvedTikNumbers,
                    ambiguousResolutions: ambiguousTikNumbers),
                resolutionEngine ?? new FakeResolutionEngine(),
                graphClient ?? new FakeFilingGraphClient(),
                msgGenerator ?? new FakeMsgGenerator(),
                documentWriter ?? new FakeDocumentWriter(),
                Options.Create(filingSettings),
                Options.Create(automationSettings),
                NullLogger<EmailFilingService>.Instance,
                emailNotifier ?? new FakeEmailNotifier(),
                new FixedTimeProvider(ReceivedUtc));
        }

        private static async Task AssertPrimaryObserverOnlyAsync(
            string subject,
            EmailCaseResolutionSnapshot snapshot,
            string expectedClassification)
        {
            await using var db = CreateDb();
            var graph = new FakeFilingGraphClient();
            var msgGenerator = new FakeMsgGenerator();
            var documentWriter = new FakeDocumentWriter();
            var service = CreateService(
                db,
                new Dictionary<string, int>(),
                dryRun: false,
                realWriteEnabled: true,
                primaryResolver: new FakePrimaryResolver(snapshot),
                graphClient: graph,
                msgGenerator: msgGenerator,
                documentWriter: documentWriter);

            var diagnostic = await service.ProcessAsync(
                "mailbox@odmon.example",
                [],
                Message(subject),
                CancellationToken.None);

            Assert.NotNull(diagnostic);
            Assert.Contains(expectedClassification, diagnostic!.ObserverClassifications);
            Assert.Empty(diagnostic.Targets);
            Assert.Equal(0, graph.MimeFetchCount);
            Assert.Equal(0, msgGenerator.GenerateCount);
            Assert.Empty(documentWriter.CreatedTikCounters);
            Assert.Empty(documentWriter.WrittenTikCounters);
        }

        private static EmailAutomationMessage Message(
            string subject,
            string? body = "Synthetic body",
            string? bodyContentType = "text",
            string? sender = "sender@odmon.example")
            => new(
                "graph-synthetic-1",
                "<synthetic-1@odmon.example>",
                subject,
                sender,
                ["recipient@odmon.example"],
                ["copy@odmon.example"],
                ReceivedUtc,
                Body: body,
                BodyContentType: bodyContentType,
                BccRecipients: ["blind@odmon.example"]);

        private static string DirectInsuranceBody(
            string claimNumber,
            string? freeTextTik = null)
            =>
                "פרטי צד ג : תביעה - SYNTHETIC-SECONDARY\r\n" +
                "מספר רכב - 12-345-67\r\n" +
                $"תיק תביעה מספר : {claimNumber}\r\n" +
                "תאריך אירוע : 30/08/2026\r\n" +
                (freeTextTik == null ? "synthetic correspondence" : $"synthetic correspondence {freeTextTik}");

        private static IntegrationDbContext CreateDb()
        {
            var options = new DbContextOptionsBuilder<IntegrationDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options;
            return new IntegrationDbContext(options);
        }

        private sealed class FakeOdcanitReader(
            IReadOnlyDictionary<string, int> resolutions,
            IReadOnlyDictionary<string, IReadOnlyList<int>>? ambiguousResolutions = null)
            : IOdcanitReader
        {
            public Task<Dictionary<string, int>> ResolveTikNumbersToCountersAsync(
                IEnumerable<string> tikNumbers,
                CancellationToken ct)
                => Task.FromResult(tikNumbers
                    .Where(resolutions.ContainsKey)
                    .ToDictionary(value => value, value => resolutions[value], StringComparer.Ordinal));

            public Task<Dictionary<string, TikNumberResolution>> ResolveTikNumbersWithAmbiguityAsync(
                IEnumerable<string> tikNumbers,
                CancellationToken ct)
            {
                var result = new Dictionary<string, TikNumberResolution>(StringComparer.Ordinal);
                foreach (var tikNumber in tikNumbers.Distinct(StringComparer.Ordinal))
                {
                    if (ambiguousResolutions?.TryGetValue(tikNumber, out var counters) == true)
                    {
                        var distinctCounters = counters.Distinct().ToArray();
                        result[tikNumber] = distinctCounters.Length == 1
                            ? new TikNumberResolution(distinctCounters[0], IsAmbiguous: false)
                            : new TikNumberResolution(null, IsAmbiguous: true);
                    }
                    else if (resolutions.TryGetValue(tikNumber, out var counter))
                    {
                        result[tikNumber] = new TikNumberResolution(counter, IsAmbiguous: false);
                    }
                }

                return Task.FromResult(result);
            }

            public Task<List<OdcanitCase>> GetCasesCreatedOnDateAsync(DateTime date, CancellationToken ct)
                => Task.FromResult(new List<OdcanitCase>());

            public Task<List<OdcanitCase>> GetCasesByTikCountersAsync(IEnumerable<int> tikCounters, CancellationToken ct)
            {
                var requested = tikCounters.ToHashSet();
                return Task.FromResult(resolutions
                    .Where(pair => requested.Contains(pair.Value))
                    .Select(pair => new OdcanitCase
                    {
                        TikCounter = pair.Value,
                        TikNumber = pair.Key
                    })
                    .ToList());
            }

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

        private sealed class FakePrimaryResolver(
            EmailCaseResolutionSnapshot? snapshot = null,
            IReadOnlyDictionary<string, int>? resolutions = null,
            IReadOnlyDictionary<string, IReadOnlyList<int>>? ambiguousResolutions = null)
            : IEmailCasePrimaryResolver
        {
            public int CallCount { get; private set; }

            public Task<EmailCaseResolutionSnapshot> ResolveAsync(
                EmailCaseEvidence evidence,
                CancellationToken cancellationToken)
            {
                CallCount++;
                if (snapshot != null)
                    return Task.FromResult(snapshot);

                var tikResults = evidence.InternalTikNumbers
                    .Select(value => value.NormalizedValue)
                    .Distinct(StringComparer.Ordinal)
                    .Select(value =>
                    {
                        if (ambiguousResolutions?.TryGetValue(value, out var ambiguous) == true)
                        {
                            return new EvidenceResolutionResult(
                                EmailEvidenceType.InternalTikNumber,
                                value,
                                ambiguous);
                        }

                        return new EvidenceResolutionResult(
                            EmailEvidenceType.InternalTikNumber,
                            value,
                            resolutions?.TryGetValue(value, out var counter) == true
                                ? [counter]
                                : []);
                    })
                    .ToArray();
                return Task.FromResult(new EmailCaseResolutionSnapshot(tikResults, [], []));
            }
        }

        private sealed class ThrowingPrimaryResolver : IEmailCasePrimaryResolver
        {
            public Task<EmailCaseResolutionSnapshot> ResolveAsync(
                EmailCaseEvidence evidence,
                CancellationToken cancellationToken)
                => throw new InvalidOperationException("SYNTHETIC-PRIVATE-VALUE");
        }

        private sealed class FakeResolutionEngine : IEmailCaseResolutionEngine
        {
            public Task<EmailCaseResolutionAnalysis> AnalyzeAsync(
                EmailCaseEvidence evidence,
                EmailCaseResolutionSnapshot primarySnapshot,
                CancellationToken cancellationToken)
                => Task.FromResult(
                    EmailCaseResolutionAnalysis.WithoutSupportingNarrowing(primarySnapshot));
        }

        private sealed class FakeSupportingResolutionRepository(
            IReadOnlySet<int> vehicleMatches)
            : IEmailCaseResolutionRepository
        {
            public int SupportingCallCount { get; private set; }

            public Task<IReadOnlyList<EvidenceResolutionResult>> ResolveInternalTikNumbersAsync(
                IEnumerable<string> normalizedValues,
                CancellationToken cancellationToken)
                => EmptyPrimary();

            public Task<IReadOnlyList<EvidenceResolutionResult>> ResolveClaimNumbersAsync(
                IEnumerable<string> normalizedValues,
                CancellationToken cancellationToken)
                => EmptyPrimary();

            public Task<IReadOnlyList<EvidenceResolutionResult>> ResolveCourtCaseNumbersAsync(
                IEnumerable<string> normalizedValues,
                CancellationToken cancellationToken)
                => EmptyPrimary();

            public Task<SupportingEvidenceFilterResult> FilterCandidatesByVehicleAsync(
                IEnumerable<int> candidateTikCounters,
                IEnumerable<string> normalizedValues,
                CancellationToken cancellationToken)
            {
                SupportingCallCount++;
                var candidates = candidateTikCounters.ToHashSet();
                return Task.FromResult(new SupportingEvidenceFilterResult(
                    candidates,
                    vehicleMatches.Where(candidates.Contains).ToHashSet()));
            }

            public Task<SupportingEvidenceFilterResult> FilterCandidatesByEventDateAsync(
                IEnumerable<int> candidateTikCounters,
                IEnumerable<string> normalizedValues,
                CancellationToken cancellationToken)
                => EmptySet();

            public Task<SupportingEvidenceFilterResult> FilterCandidatesByClientAsync(
                IEnumerable<int> candidateTikCounters,
                IEnumerable<string> normalizedValues,
                CancellationToken cancellationToken)
                => EmptySet();

            public Task<SupportingEvidenceFilterResult> FilterCandidatesByInsuredNameAsync(
                IEnumerable<int> candidateTikCounters,
                IEnumerable<string> normalizedValues,
                CancellationToken cancellationToken)
                => EmptySet();

            public Task<SupportingEvidenceFilterResult> FilterCandidatesByDriverPhoneAsync(
                IEnumerable<int> candidateTikCounters,
                IEnumerable<string> normalizedValues,
                CancellationToken cancellationToken)
                => EmptySet();

            private static Task<IReadOnlyList<EvidenceResolutionResult>> EmptyPrimary()
                => Task.FromResult<IReadOnlyList<EvidenceResolutionResult>>([]);

            private static Task<SupportingEvidenceFilterResult> EmptySet()
                => Task.FromResult(SupportingEvidenceFilterResult.Empty);
        }

        private sealed class FakeFilingGraphClient(params EmailAutomationMessage[] messages)
            : IEmailFilingGraphClient
        {
            public int MimeFetchCount { get; private set; }
            public int DeltaFetchCount { get; private set; }
            public byte[] MimeBytes { get; set; } = [1, 2, 3];
            public string? LastRequestedDeltaLink { get; private set; }
            public DateTime LastProcessingFromUtc { get; private set; }
            public string ReturnedDeltaLink { get; set; } = "filing-delta-1";
            public Exception? DeltaException { get; set; }

            public Task<EmailAutomationDeltaPage> GetDeltaPageAsync(
                string mailbox,
                string folderId,
                string? deltaLink,
                DateTime processingFromUtc,
                int pageSize,
                CancellationToken cancellationToken)
            {
                DeltaFetchCount++;
                LastRequestedDeltaLink = deltaLink;
                LastProcessingFromUtc = processingFromUtc;
                if (DeltaException != null)
                    throw DeltaException;
                return Task.FromResult(new EmailAutomationDeltaPage(
                    messages,
                    NextLink: null,
                    DeltaLink: ReturnedDeltaLink));
            }

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

            public Task<string> ResolveDestinationPathAsync(
                int docCounter,
                CancellationToken cancellationToken)
                => Task.FromResult($@"C:\synthetic\{docCounter - 100000}.msg");

            public Task<bool> IsVerifiedDestinationAsync(
                string destinationPath,
                long expectedLength,
                CancellationToken cancellationToken)
                => Task.FromResult(VerifiedDestinations.Contains(destinationPath));

            public Task CopyAndVerifyAsync(
                string msgFilePath,
                string destinationPath,
                long expectedLength,
                bool overwriteExisting,
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
