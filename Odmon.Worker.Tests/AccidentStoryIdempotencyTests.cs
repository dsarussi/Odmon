using Microsoft.EntityFrameworkCore;
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
            var repo = new CaseAnnexWriteStateRepository(db);

            var state = await repo.GetOrCreateStateAsync(39283, default);

            Assert.NotNull(state);
            Assert.Equal(39283, state.TikCounter);
            Assert.False(state.AccidentStoryAnnexWritten);
            Assert.Null(state.AccidentStoryAnnexWrittenAtUtc);
        }

        [Fact]
        public async Task MarkAccidentStoryWrittenAsync_ThenGetOrCreate_ReturnsWrittenTrue()
        {
            await using var db = CreateInMemoryContext();
            var repo = new CaseAnnexWriteStateRepository(db);

            await repo.GetOrCreateStateAsync(39283, default);
            await repo.MarkAccidentStoryWrittenAsync(39283, "run-abc", default);

            var state = await repo.GetOrCreateStateAsync(39283, default);
            Assert.True(state.AccidentStoryAnnexWritten);
            Assert.NotNull(state.AccidentStoryAnnexWrittenAtUtc);
            Assert.Equal("run-abc", state.AccidentStoryAnnexWrittenRunId);
        }

        [Fact]
        public async Task GetOrCreateStateAsync_AlreadyWritten_ReturnsSameState()
        {
            await using var db = CreateInMemoryContext();
            var repo = new CaseAnnexWriteStateRepository(db);
            await repo.GetOrCreateStateAsync(1, default);
            await repo.MarkAccidentStoryWrittenAsync(1, "run-1", default);

            var state = await repo.GetOrCreateStateAsync(1, default);

            Assert.True(state.AccidentStoryAnnexWritten);
        }
    }
}
