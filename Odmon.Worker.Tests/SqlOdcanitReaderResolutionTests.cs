using Odmon.Worker.OdcanitAccess;
using Xunit;

namespace Odmon.Worker.Tests
{
    public class SqlOdcanitReaderResolutionTests
    {
        [Fact]
        public async Task ResolveTikNumbersInBatchesAsync_LargeInput_UsesBoundedBatchesAndMergesResults()
        {
            var tikNumbers = Enumerable.Range(1, 2505)
                .Select(i => $"TEST-TIK-{i:D5}")
                .ToList();
            var batchSizes = new List<int>();

            var resolved = await SqlOdcanitReader.ResolveTikNumbersInBatchesAsync(
                tikNumbers,
                SqlOdcanitReader.TikNumberResolutionBatchSize,
                (batch, _) =>
                {
                    batchSizes.Add(batch.Count);
                    Assert.InRange(batch.Count, 1, SqlOdcanitReader.TikNumberResolutionBatchSize);
                    return Task.FromResult(batch.ToDictionary(
                        tikNumber => tikNumber,
                        tikNumber => int.Parse(tikNumber[^5..]),
                        StringComparer.Ordinal));
                },
                CancellationToken.None);

            Assert.Equal(new[] { 1000, 1000, 505 }, batchSizes);
            Assert.Equal(tikNumbers.Count, resolved.Count);
            foreach (var tikNumber in tikNumbers)
            {
                Assert.Equal(int.Parse(tikNumber[^5..]), resolved[tikNumber]);
            }
        }

        [Fact]
        public async Task ResolveTikNumbersInBatchesAsync_SmallInput_UsesSingleBatch()
        {
            var tikNumbers = new[] { "TEST-TIK-00001", "TEST-TIK-00002" };
            var calls = 0;

            var resolved = await SqlOdcanitReader.ResolveTikNumbersInBatchesAsync(
                tikNumbers,
                SqlOdcanitReader.TikNumberResolutionBatchSize,
                (batch, _) =>
                {
                    calls++;
                    return Task.FromResult(batch.ToDictionary(
                        tikNumber => tikNumber,
                        _ => 1,
                        StringComparer.Ordinal));
                },
                CancellationToken.None);

            Assert.Equal(1, calls);
            Assert.Equal(2, resolved.Count);
        }

        [Fact]
        public async Task ResolveTikNumbersInBatchesAsync_EmptyInput_DoesNotQuery()
        {
            var calls = 0;

            var resolved = await SqlOdcanitReader.ResolveTikNumbersInBatchesAsync(
                Array.Empty<string>(),
                SqlOdcanitReader.TikNumberResolutionBatchSize,
                (_, _) =>
                {
                    calls++;
                    return Task.FromResult(new Dictionary<string, int>(StringComparer.Ordinal));
                },
                CancellationToken.None);

            Assert.Equal(0, calls);
            Assert.Empty(resolved);
        }

        [Fact]
        public void NormalizeTikNumbers_TrimsRemovesEmptyAndDeduplicatesOrdinally()
        {
            var normalized = SqlOdcanitReader.NormalizeTikNumbers(new[]
            {
                " TEST-TIK-00001 ",
                "",
                "   ",
                "TEST-TIK-00001",
                "test-tik-00001",
                "TEST-TIK-00002"
            });

            Assert.Equal(
                new[] { "TEST-TIK-00001", "test-tik-00001", "TEST-TIK-00002" },
                normalized);
        }

        [Fact]
        public void ClassifyTikNumberMatches_RejectsDifferentCountersAsAmbiguous()
        {
            var resolutions = SqlOdcanitReader.ClassifyTikNumberMatches(
            [
                ("9/1984", 40514),
                ("9/1984", 60002)
            ]);

            var resolution = Assert.Single(resolutions).Value;
            Assert.True(resolution.IsAmbiguous);
            Assert.False(resolution.IsResolved);
            Assert.Null(resolution.TikCounter);
        }

        [Fact]
        public void ClassifyTikNumberMatches_DuplicateSameCounterRemainsUnique()
        {
            var resolutions = SqlOdcanitReader.ClassifyTikNumberMatches(
            [
                ("9/1984", 40514),
                ("9/1984", 40514)
            ]);

            var resolution = Assert.Single(resolutions).Value;
            Assert.False(resolution.IsAmbiguous);
            Assert.True(resolution.IsResolved);
            Assert.Equal(40514, resolution.TikCounter);
        }

        [Fact]
        public async Task ResolveTikNumbersInBatchesAsync_CanceledToken_StopsBeforeQuery()
        {
            using var cts = new CancellationTokenSource();
            cts.Cancel();
            var calls = 0;

            await Assert.ThrowsAsync<OperationCanceledException>(() =>
                SqlOdcanitReader.ResolveTikNumbersInBatchesAsync(
                    new[] { "TEST-TIK-00001" },
                    SqlOdcanitReader.TikNumberResolutionBatchSize,
                    (_, _) =>
                    {
                        calls++;
                        return Task.FromResult(new Dictionary<string, int>(StringComparer.Ordinal));
                    },
                    cts.Token));

            Assert.Equal(0, calls);
        }

        [Fact]
        public async Task ResolveTikNumbersInBatchesAsync_BatchFailure_Propagates()
        {
            var expected = new InvalidOperationException("Synthetic batch failure");

            var actual = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                SqlOdcanitReader.ResolveTikNumbersInBatchesAsync(
                    new[] { "TEST-TIK-00001" },
                    SqlOdcanitReader.TikNumberResolutionBatchSize,
                    (_, _) => Task.FromException<Dictionary<string, int>>(expected),
                    CancellationToken.None));

            Assert.Same(expected, actual);
        }
    }
}
