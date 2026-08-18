using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Odmon.Worker.Data;
using Odmon.Worker.Models;
using Odmon.Worker.Services;
using Xunit;

namespace Odmon.Worker.Tests
{
    /// <summary>
    /// Idempotency for Accident Story annex write: state repo and behaviour (AlreadyWritten => skip; after success => flag set).
    /// Uses in-memory IntegrationDbContext; no real DB/HTTP.
    /// </summary>
    public class AccidentStoryIdempotencyTests
    {
        private static IntegrationDbContext CreateInMemoryContext()
        {
            var options = new DbContextOptionsBuilder<IntegrationDbContext>()
                .UseInMemoryDatabase(databaseName: "AccidentStoryIdempotency_" + Guid.NewGuid().ToString("N")[..8])
                .Options;
            return new IntegrationDbContext(options);
        }

        [Fact]
        public async Task GetOrCreateStateAsync_NoRow_CreatesAndReturnsUnwritten()
        {
            await using var db = CreateInMemoryContext();
            var repo = new CaseAnnexWriteStateRepository(db, NullLogger<CaseAnnexWriteStateRepository>.Instance);

            var state = await repo.GetOrCreateStateAsync(91001, default);

            Assert.NotNull(state);
            Assert.Equal(91001, state.TikCounter);
            Assert.False(state.AccidentStoryAnnexWritten);
            Assert.Null(state.AccidentStoryAnnexWrittenAtUtc);
        }

        [Fact]
        public async Task MarkAccidentStoryWrittenAsync_ThenGetOrCreate_ReturnsWrittenTrue()
        {
            await using var db = CreateInMemoryContext();
            var repo = new CaseAnnexWriteStateRepository(db, NullLogger<CaseAnnexWriteStateRepository>.Instance);

            await repo.GetOrCreateStateAsync(91001, default);
            await repo.MarkAccidentStoryWrittenAsync(91001, "run-abc", default);

            var state = await repo.GetOrCreateStateAsync(91001, default);
            Assert.True(state.AccidentStoryAnnexWritten);
            Assert.NotNull(state.AccidentStoryAnnexWrittenAtUtc);
            Assert.Equal("run-abc", state.AccidentStoryAnnexWrittenRunId);
        }

        [Fact]
        public async Task GetOrCreateStateAsync_AlreadyWritten_ReturnsSameState()
        {
            await using var db = CreateInMemoryContext();
            var repo = new CaseAnnexWriteStateRepository(db, NullLogger<CaseAnnexWriteStateRepository>.Instance);
            await repo.GetOrCreateStateAsync(1, default);
            await repo.MarkAccidentStoryWrittenAsync(1, "run-1", default);

            var state = await repo.GetOrCreateStateAsync(1, default);

            Assert.True(state.AccidentStoryAnnexWritten);
        }

        /// <summary>
        /// When dedup hit (2601/2627) is handled, we mark the case written so it is not re-processed.
        /// This test verifies that calling MarkAccidentStoryWrittenAsync (as the dedup-handler does) sets Written + AtUtc + RunId.
        /// </summary>
        [Fact]
        public async Task DedupHit_MarkWritten_SetsFlagAndStopsReprocessing()
        {
            await using var db = CreateInMemoryContext();
            var repo = new CaseAnnexWriteStateRepository(db, NullLogger<CaseAnnexWriteStateRepository>.Instance);
            await repo.GetOrCreateStateAsync(91001, default);

            // Simulate what DocumentIngestionService does on dedup hit (2601/2627): mark written so next run skips.
            await repo.MarkAccidentStoryWrittenAsync(91001, "run-dedup-hit", default);

            var state = await repo.GetOrCreateStateAsync(91001, default);
            Assert.True(state.AccidentStoryAnnexWritten);
            Assert.NotNull(state.AccidentStoryAnnexWrittenAtUtc);
            Assert.Equal("run-dedup-hit", state.AccidentStoryAnnexWrittenRunId);
        }

        /// <summary>
        /// When state is already written, IsAccidentStoryAlreadyWrittenAsync returns true so ProcessAccidentStoryAsync skips without calling the writer.
        /// Uses a fake repo that returns Written=true.
        /// </summary>
        [Fact]
        public async Task IsAccidentStoryAlreadyWrittenAsync_WhenStateWritten_ReturnsTrue()
        {
            var fakeRepo = new FakeStateRepoWrittenTrue();
            var result = await DocumentIngestionService.IsAccidentStoryAlreadyWrittenAsync(fakeRepo, 91001, default);
            Assert.True(result);
        }

        /// <summary>
        /// Success path: when MarkAccidentStoryWrittenAsync is called exactly once, state is persisted (so no repeated writes).
        /// </summary>
        [Fact]
        public async Task SuccessPath_MarkAccidentStoryWrittenAsync_CalledOnce_StatePersisted()
        {
            await using var db = CreateInMemoryContext();
            var repo = new CaseAnnexWriteStateRepository(db, NullLogger<CaseAnnexWriteStateRepository>.Instance);
            await repo.GetOrCreateStateAsync(91001, default);

            await repo.MarkAccidentStoryWrittenAsync(91001, "run-success", default);

            var state = await repo.GetOrCreateStateAsync(91001, default);
            Assert.True(state.AccidentStoryAnnexWritten);
            Assert.NotNull(state.AccidentStoryAnnexWrittenAtUtc);
            Assert.Equal("run-success", state.AccidentStoryAnnexWrittenRunId);
        }

        private sealed class FakeStateRepoWrittenTrue : ICaseAnnexWriteStateRepository
        {
            public Task<CaseAnnexWriteState> GetOrCreateStateAsync(int tikCounter, CancellationToken ct = default) =>
                Task.FromResult(new CaseAnnexWriteState { TikCounter = tikCounter, AccidentStoryAnnexWritten = true });

            public Task MarkAccidentStoryWrittenAsync(int tikCounter, string? runId = null, CancellationToken ct = default) =>
                Task.CompletedTask;
        }
    }
}
