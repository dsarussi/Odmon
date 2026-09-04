using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Odmon.Worker.Configuration;
using Odmon.Worker.Data;
using Odmon.Worker.Models;
using Odmon.Worker.Services;
using Xunit;

namespace Odmon.Worker.Tests;

public sealed partial class EmailFilingTests
{
    [Fact]
    public async Task DeltaFirstCyclePersistsStateAndEmptySecondCycleDoesNotResolveAgain()
    {
        await using var db = CreateDb();
        var graph = new ScriptedFilingGraphClient();
        graph.Add(null, new([Message("Synthetic update")], null, "delta-1"));
        graph.Add("delta-1", new([], null, "delta-2"));
        var polling = CreatePollingService(db, graph, DeltaSettings(), ReceivedUtc);

        await polling.RunAsync(CancellationToken.None);
        var firstState = await db.EmailFilingMailboxStates.AsNoTracking().SingleAsync();
        Assert.Equal("delta-1", firstState.DeltaLink);
        Assert.NotNull(firstState.LastSuccessfulSyncUtc);
        Assert.Single(db.EmailFilingResolutionRuns);

        await polling.RunAsync(CancellationToken.None);

        Assert.Single(db.EmailFilingResolutionRuns);
        Assert.Equal("delta-2", (await db.EmailFilingMailboxStates.AsNoTracking().SingleAsync()).DeltaLink);
        Assert.Equal(new string?[] { null, "delta-1" }, graph.RequestedCursors);
    }

    [Fact]
    public async Task DeltaRestartReadsPersistedStateAndDoesNotReplayInitialBatch()
    {
        var options = new DbContextOptionsBuilder<IntegrationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        var graph = new ScriptedFilingGraphClient();
        graph.Add(null, new([Message("Synthetic update")], null, "delta-1"));
        graph.Add("delta-1", new([], null, "delta-2"));
        await using (var firstDb = new IntegrationDbContext(options))
        {
            await CreatePollingService(firstDb, graph, DeltaSettings(), ReceivedUtc)
                .RunAsync(CancellationToken.None);
        }

        await using var restartedDb = new IntegrationDbContext(options);
        await CreatePollingService(restartedDb, graph, DeltaSettings(), ReceivedUtc.AddMinutes(3))
            .RunAsync(CancellationToken.None);

        Assert.Single(restartedDb.EmailFilingMailboxStates);
        Assert.Single(restartedDb.EmailFilingResolutionRuns);
        Assert.Equal("delta-2", (await restartedDb.EmailFilingMailboxStates.AsNoTracking().SingleAsync()).DeltaLink);
        Assert.Equal(new string?[] { null, "delta-1" }, graph.RequestedCursors);
    }

    [Fact]
    public async Task DeltaPaginationTraversesEmptyPagesAndPersistsOnlyFinalDeltaLink()
    {
        await using var db = CreateDb();
        var graph = new ScriptedFilingGraphClient();
        graph.Add(null, new([Message("First")], "page-2", null));
        graph.Add("page-2", new([], "page-3", null));
        graph.Add("page-3", new([Message("Second") with { InternetMessageId = "<second@odmon.example>" }], null, "delta-final"));
        graph.BeforeRequest = _ => Assert.Null(db.EmailFilingMailboxStates.AsNoTracking().Single().DeltaLink);

        await CreatePollingService(db, graph, DeltaSettings(), ReceivedUtc).RunAsync(CancellationToken.None);

        Assert.Equal(2, db.EmailFilingResolutionRuns.Count());
        Assert.Equal("delta-final", (await db.EmailFilingMailboxStates.AsNoTracking().SingleAsync()).DeltaLink);
        Assert.Equal(new string?[] { null, "page-2", "page-3" }, graph.RequestedCursors);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DeltaTraversalFailurePreservesPersistedCursorAndSuccessfulSyncTime(bool missingFinalLink)
    {
        await using var db = CreateDb();
        var previousSync = ReceivedUtc.AddMinutes(-3);
        db.EmailFilingMailboxStates.Add(new()
        {
            Mailbox = "mailbox@odmon.example", FolderId = "Inbox", DeltaLink = "delta-old",
            ProcessingFromUtc = ReceivedUtc.AddDays(-1), LastSuccessfulSyncUtc = previousSync,
            CreatedAtUtc = previousSync, UpdatedAtUtc = previousSync
        });
        await db.SaveChangesAsync();
        var graph = new ScriptedFilingGraphClient();
        graph.Add("delta-old", new([Message("Synthetic update")], "page-2", null));
        graph.AddResponse("page-2", () => missingFinalLink
            ? new([], null, null)
            : throw new HttpRequestException("Synthetic traversal failure"));
        var polling = CreatePollingService(db, graph, DeltaSettings(), ReceivedUtc);

        await Assert.ThrowsAnyAsync<Exception>(() => polling.RunAsync(CancellationToken.None));

        var state = await db.EmailFilingMailboxStates.AsNoTracking().SingleAsync();
        Assert.Equal("delta-old", state.DeltaLink);
        Assert.Equal(previousSync, state.LastSuccessfulSyncUtc);
        Assert.Equal(previousSync, state.UpdatedAtUtc);
        Assert.Single(db.EmailFilingResolutionRuns);
    }

    [Fact]
    public async Task DeltaUsesNormalizedMailboxFolderAndLeavesExistingForwardingStateUntouched()
    {
        await using var db = CreateDb();
        var baseline = ReceivedUtc.AddDays(-1);
        db.EmailAutomationMailboxStates.Add(new()
        {
            Mailbox = "mailbox@odmon.example", FolderId = "Inbox", DeltaLink = "forwarding-delta",
            ProcessingFromUtc = baseline, CreatedAtUtc = baseline, UpdatedAtUtc = baseline
        });
        await db.SaveChangesAsync();
        var graph = new ScriptedFilingGraphClient();
        graph.Add(null, new([Message("Synthetic update")], null, "filing-delta"));
        graph.Add("filing-delta", new([], null, "filing-delta-2"));
        var polling = CreatePollingService(db, graph, DeltaSettings(), ReceivedUtc);
        var mailbox = new EmailAutomationMailboxSettings { Address = " MAILBOX@odmon.example ", InboxFolder = " Inbox " };

        await polling.ProcessMailboxAsync(mailbox, CancellationToken.None);
        await polling.RunAsync(CancellationToken.None);

        Assert.Single(db.EmailFilingMailboxStates);
        var state = await db.EmailAutomationMailboxStates.AsNoTracking().SingleAsync();
        Assert.Equal("forwarding-delta", state.DeltaLink);
        Assert.Equal(baseline, state.UpdatedAtUtc);
        Assert.Null(state.LastSuccessfulSyncUtc);
        Assert.Empty(db.EmailAutomationLogs);
    }

    [Fact]
    public async Task DeltaUnchangedRedeliveryDoesNotCreateAnotherResolutionRun()
    {
        await using var db = CreateDb();
        var graph = new ScriptedFilingGraphClient();
        var message = Message("Synthetic update");
        graph.Add(null, new([message], null, "delta-1"));
        graph.Add("delta-1", new([message], null, "delta-2"));
        var polling = CreatePollingService(db, graph, DeltaSettings(), ReceivedUtc);

        await polling.RunAsync(CancellationToken.None);
        await polling.RunAsync(CancellationToken.None);

        Assert.Single(db.EmailFilingResolutionRuns);
        Assert.Equal("delta-2", (await db.EmailFilingMailboxStates.AsNoTracking().SingleAsync()).DeltaLink);
    }

    [Fact]
    public async Task DeltaFailedTraversalRetryAfterRestartDoesNotResolveSuccessfulPrefixAgain()
    {
        var options = new DbContextOptionsBuilder<IntegrationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        var graph = new ScriptedFilingGraphClient();
        for (var cycle = 0; cycle < 2; cycle++)
        {
            graph.Add(null, new([Message("Synthetic completed message")], "page-2", null));
            graph.AddResponse("page-2", () => throw new HttpRequestException("Synthetic traversal failure"));
            await using var db = new IntegrationDbContext(options);
            var polling = CreatePollingService(db, graph, DeltaSettings(), ReceivedUtc);
            await Assert.ThrowsAsync<HttpRequestException>(() => polling.RunAsync(CancellationToken.None));
        }

        await using var verificationDb = new IntegrationDbContext(options);
        Assert.Single(verificationDb.EmailFilingResolutionRuns);
        var state = await verificationDb.EmailFilingMailboxStates.SingleAsync();
        Assert.Null(state.DeltaLink);
        Assert.Null(state.LastSuccessfulSyncUtc);
    }

    [Fact]
    public async Task DeltaCycleLimitCheckpointsNextLinkAndRestartStoresFinalDeltaOnlyOnCompletion()
    {
        var options = new DbContextOptionsBuilder<IntegrationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        var graph = new ScriptedFilingGraphClient();
        graph.Add(null, new([Message("First")], "page-2", null));
        graph.AddResponse("page-2", () => throw new HttpRequestException("Synthetic page failure"));
        graph.Add("page-2", new([Message("Second") with { InternetMessageId = "<second@odmon.example>" }], null, "delta-final"));
        graph.Add("delta-final", new([], null, "delta-empty"));
        await using (var db = new IntegrationDbContext(options))
        {
            await CreatePollingService(db, graph, DeltaSettings(1), ReceivedUtc).RunAsync(CancellationToken.None);
            var state = await db.EmailFilingMailboxStates.AsNoTracking().SingleAsync();
            Assert.Null(state.DeltaLink);
            Assert.Equal("page-2", state.NextLink);
            Assert.Null(state.LastSuccessfulSyncUtc);
        }
        await using (var db = new IntegrationDbContext(options))
        {
            var polling = CreatePollingService(db, graph, DeltaSettings(1), ReceivedUtc);
            await Assert.ThrowsAsync<HttpRequestException>(() => polling.RunAsync(CancellationToken.None));
            var state = await db.EmailFilingMailboxStates.AsNoTracking().SingleAsync();
            Assert.Null(state.DeltaLink);
            Assert.Equal("page-2", state.NextLink);
            Assert.Null(state.LastSuccessfulSyncUtc);
        }
        await using var restartedDb = new IntegrationDbContext(options);
        var restarted = CreatePollingService(restartedDb, graph, DeltaSettings(1), ReceivedUtc.AddMinutes(3));
        await restarted.RunAsync(CancellationToken.None);
        var completed = await restartedDb.EmailFilingMailboxStates.AsNoTracking().SingleAsync();
        Assert.Equal("delta-final", completed.DeltaLink);
        Assert.Null(completed.NextLink);
        Assert.NotNull(completed.LastSuccessfulSyncUtc);
        await restarted.RunAsync(CancellationToken.None);
        Assert.Equal(2, restartedDb.EmailFilingResolutionRuns.Count());
        Assert.Equal(new string?[] { null, "page-2", "page-2", "delta-final" }, graph.RequestedCursors);
    }

    [Fact]
    public async Task DeltaChangedContentAndRevertedContentReenterProcessingButGraphIdChangeDoesNot()
    {
        await using var db = CreateDb();
        var original = Message("Synthetic original");
        var changed = original with { Body = "Synthetic updated body" };
        var graph = new ScriptedFilingGraphClient();
        graph.Add(null, new([original], null, "delta-1"));
        graph.Add("delta-1", new([original with { Id = "new-graph-id" }], null, "delta-2"));
        graph.Add("delta-2", new([changed], null, "delta-3"));
        graph.Add("delta-3", new([original], null, "delta-4"));
        var polling = CreatePollingService(db, graph, DeltaSettings(), ReceivedUtc);

        await polling.RunAsync(CancellationToken.None);
        await polling.RunAsync(CancellationToken.None);
        Assert.Single(db.EmailFilingResolutionRuns);
        await polling.RunAsync(CancellationToken.None);
        await polling.RunAsync(CancellationToken.None);

        Assert.Equal(3, db.EmailFilingResolutionRuns.Count());
        Assert.Single(db.EmailFilingDiagnostics.Select(row => row.MessageFingerprint).Distinct());
        Assert.Equal(2, db.EmailFilingDiagnostics.Select(row => row.ProcessedContentFingerprint).Distinct().Count());
    }

    [Fact]
    public async Task DeltaMessageFailureRetriesOnlyFailedMessageAndKeepsWriteDedup()
    {
        await using var db = CreateDb();
        var settings = DeltaSettings();
        var success = Message("No filing target");
        var failing = Message("9/1984") with { InternetMessageId = "<failing@odmon.example>" };
        var graph = new ScriptedFilingGraphClient();
        var mimeGraph = new FakeFilingGraphClient();
        var generator = new FakeMsgGenerator { ThrowOnGenerate = true };
        var writer = new FakeDocumentWriter();
        var service = CreateService(db, new Dictionary<string, int> { ["9/1984"] = 40514 },
            dryRun: false, realWriteEnabled: true, graphClient: mimeGraph,
            msgGenerator: generator, documentWriter: writer);
        var polling = CreatePollingService(db, graph, settings, ReceivedUtc, service);

        for (var cycle = 0; cycle < 2; cycle++)
        {
            graph.Add(null, new([success, failing], null, "delta-1"));
            await Assert.ThrowsAsync<EmailFilingProcessingException>(() => polling.RunAsync(CancellationToken.None));
            Assert.Null((await db.EmailFilingMailboxStates.AsNoTracking().SingleAsync()).DeltaLink);
        }
        Assert.Equal(3, db.EmailFilingResolutionRuns.Count());
        Assert.Single(db.EmailFilingDiagnostics.Where(row => row.ProcessedContentFingerprint != null));
        Assert.Empty(db.EmailFilingDedups);

        generator.ThrowOnGenerate = false;
        graph.Add(null, new([success, failing], null, "delta-1"));
        await polling.RunAsync(CancellationToken.None);
        Assert.Single(db.EmailFilingDedups);
        Assert.Single(writer.CreatedTikCounters);
        Assert.Equal(4, db.EmailFilingResolutionRuns.Count());

        // A meaningful content change still enters resolution. Existing write
        // dedup must prevent a second write for the same message and target.
        graph.Add("delta-1", new([failing with { Body = "Changed synthetic content" }], null, "delta-2"));
        await polling.RunAsync(CancellationToken.None);
        Assert.Equal(5, db.EmailFilingResolutionRuns.Count());
        Assert.Single(db.EmailFilingDedups);
        Assert.Single(writer.CreatedTikCounters);
        Assert.Equal(1, db.EmailFilingDiagnostics.OrderByDescending(row => row.Id).First().DedupHitCount);
    }

    [Fact]
    public async Task DeltaLegacyDiagnosticWithoutContentFingerprintIsProcessedOnceThenSkipped()
    {
        await using var db = CreateDb();
        var message = Message("Synthetic legacy message");
        await CreateService(db, new Dictionary<string, int>()).ProcessAsync(
            "mailbox@odmon.example", [], message, CancellationToken.None);
        var graph = new ScriptedFilingGraphClient();
        graph.Add(null, new([message, message], null, "delta-1"));
        var polling = CreatePollingService(db, graph, DeltaSettings(), ReceivedUtc);

        await polling.RunAsync(CancellationToken.None);

        Assert.Equal(2, db.EmailFilingResolutionRuns.Count());
        Assert.Single(db.EmailFilingDiagnostics.Where(row => row.ProcessedContentFingerprint != null));
    }

    [Fact]
    public void DeltaContentFingerprintIsKeyedAndExcludesDeliveryMetadata()
    {
        var original = Message("Synthetic subject");
        var fingerprint = EmailFilingPollingService.CreateContentFingerprint(original, "synthetic-key");
        Assert.Matches("^[A-F0-9]{64}$", fingerprint);
        Assert.Equal(fingerprint, EmailFilingPollingService.CreateContentFingerprint(
            original with { Id = "changed-id", InternetMessageId = "changed-message-id" }, "synthetic-key"));
        Assert.NotEqual(fingerprint, EmailFilingPollingService.CreateContentFingerprint(original, "different-key"));
        Assert.Throws<InvalidOperationException>(() => EmailFilingPollingService.CreateContentFingerprint(original, ""));
        foreach (var changed in new[]
        {
            original with { Subject = "Different" }, original with { Sender = "changed@odmon.example" },
            original with { Body = "Different" }, original with { BodyContentType = "html" },
            original with { ToRecipients = ["changed@odmon.example"] },
            original with { CcRecipients = [] }, original with { BccRecipients = [] },
            original with { SentDateTimeUtc = ReceivedUtc },
            original with { ReceivedDateTimeUtc = ReceivedUtc.AddMinutes(1) }
        })
            Assert.NotEqual(fingerprint, EmailFilingPollingService.CreateContentFingerprint(changed, "synthetic-key"));
    }

    [Fact]
    public async Task DeltaMalformedMimeIsRecordedOnceAndDoesNotBlockLaterMessages()
    {
        await using var db = CreateDb();
        var poison = Message("9/1984") with
        {
            Id = "graph-poison",
            InternetMessageId = "<poison@odmon.example>"
        };
        var valid = Message("9/1984") with
        {
            Id = "graph-valid",
            InternetMessageId = "<valid@odmon.example>"
        };
        var graph = new ScriptedFilingGraphClient();
        graph.SetMime(poison.Id,
            "From: Sender <>\r\n" +
            "To: Recipient <recipient@odmon.invalid>\r\n" +
            "Subject: Synthetic\r\n\r\nBody");
        graph.SetMime(valid.Id,
            "Date: Thu, 04 Sep 2026 10:00:00 +0000\r\n" +
            "From: Sender <sender@odmon.invalid>\r\n" +
            "To: Recipient <recipient@odmon.invalid>\r\n" +
            "Subject: Synthetic\r\nMessage-ID: <valid@odmon.invalid>\r\n" +
            "Content-Type: text/plain; charset=utf-8\r\n\r\nBody");
        var writer = new FakeDocumentWriter();
        var service = CreateService(
            db,
            new Dictionary<string, int> { ["9/1984"] = 40514 },
            dryRun: false,
            realWriteEnabled: true,
            graphClient: graph,
            msgGenerator: new EmailMsgGenerator(Options.Create(new EmailFilingSettings
            {
                MaxMimeMessageBytes = 52_428_800,
                MaxMimeAttachmentCount = 100
            })),
            documentWriter: writer);
        var polling = CreatePollingService(db, graph, DeltaSettings(), ReceivedUtc, service);
        graph.Add(null, new([poison, valid], null, "delta-1"));

        await polling.RunAsync(CancellationToken.None);

        Assert.Equal("delta-1", (await db.EmailFilingMailboxStates.AsNoTracking().SingleAsync()).DeltaLink);
        Assert.Equal(2, db.EmailFilingResolutionRuns.Count());
        Assert.Equal(2, db.EmailFilingDiagnostics.Count(row => row.ProcessedContentFingerprint != null));
        Assert.Contains(db.EmailFilingDiagnostics, row => row.FinalDecision == EmailFilingConstants.MimeFailed);
        Assert.Contains(db.EmailFilingDiagnostics, row => row.FinalDecision == EmailFilingConstants.Filed);
        Assert.Single(db.EmailFilingDedups);
        Assert.Single(writer.CreatedTikCounters);

        // Graph may replay both items. Their completed content fingerprints
        // keep them out of resolution and the completed delta still advances.
        graph.Add("delta-1", new([poison, valid], null, "delta-2"));
        await polling.RunAsync(CancellationToken.None);
        Assert.Equal(2, db.EmailFilingResolutionRuns.Count());
        Assert.Equal("delta-2", (await db.EmailFilingMailboxStates.AsNoTracking().SingleAsync()).DeltaLink);

        // A meaningful content change makes the failed message eligible again.
        var changedPoison = poison with { Body = "Changed synthetic body" };
        graph.Add("delta-2", new([changedPoison], null, "delta-3"));
        await polling.RunAsync(CancellationToken.None);
        Assert.Equal(3, db.EmailFilingResolutionRuns.Count());
        Assert.Equal(2, db.EmailFilingDiagnostics.Count(row => row.FinalDecision == EmailFilingConstants.MimeFailed));
        Assert.Single(db.EmailFilingDedups);
    }

    private static EmailFilingSettings DeltaSettings(int maxMessages = 50) => new()
    {
        Enabled = true, DryRun = true, ResolutionPhantomEnabled = true,
        StartProcessingFromUtc = ReceivedUtc.AddMinutes(-1), AllowHistoricalBackfill = true,
        MaxMessagesPerCycle = maxMessages
    };

    private sealed class ScriptedFilingGraphClient : IEmailFilingGraphClient
    {
        private readonly Queue<(string? Cursor, Func<EmailAutomationDeltaPage> Response)> _steps = new();
        private readonly Dictionary<string, byte[]> _mimeByMessageId = new(StringComparer.Ordinal);
        public List<string?> RequestedCursors { get; } = [];
        public Action<string?>? BeforeRequest { get; set; }

        public void Add(string? cursor, EmailAutomationDeltaPage page) => AddResponse(cursor, () => page);
        public void AddResponse(string? cursor, Func<EmailAutomationDeltaPage> response) => _steps.Enqueue((cursor, response));
        public void SetMime(string messageId, string mime)
            => _mimeByMessageId[messageId] = Encoding.UTF8.GetBytes(mime);

        public Task<EmailAutomationDeltaPage> GetDeltaPageAsync(
            string mailbox, string folderId, string? deltaLink, DateTime processingFromUtc,
            int pageSize, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.Equal("mailbox@odmon.example", mailbox);
            Assert.Equal("Inbox", folderId);
            Assert.NotEmpty(_steps);
            var step = _steps.Dequeue();
            Assert.Equal(step.Cursor, deltaLink);
            RequestedCursors.Add(deltaLink);
            BeforeRequest?.Invoke(deltaLink);
            return Task.FromResult(step.Response());
        }

        public Task<byte[]> GetMimeAsync(string mailbox, string messageId, CancellationToken cancellationToken)
            => Task.FromResult(_mimeByMessageId.TryGetValue(messageId, out var mime)
                ? mime
                : throw new InvalidOperationException("No MIME response configured for this synthetic message."));
    }
}
