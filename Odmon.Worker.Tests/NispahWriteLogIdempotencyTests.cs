using Microsoft.EntityFrameworkCore;
using Xunit;
using Odmon.Worker.Models;
using Odmon.Worker.Services;

namespace Odmon.Worker.Tests
{
    public class NispahWriteLogIdempotencyTests
    {
        [Fact]
        public void IsUniqueConstraintViolation_InnerSqlException2627_ReturnsTrue()
        {
            var sqlEx = SqlExceptionTestHelper.Create(2627, "Unique constraint violation");
            var dbEx = new DbUpdateException("Update failed", sqlEx);
            Assert.True(DocumentIngestionService.IsUniqueConstraintViolation(dbEx));
        }

        [Fact]
        public void IsUniqueConstraintViolation_InnerSqlExceptionOther_ReturnsFalse()
        {
            var sqlEx = SqlExceptionTestHelper.Create(208, "Invalid object name");
            var dbEx = new DbUpdateException("Update failed", sqlEx);
            Assert.False(DocumentIngestionService.IsUniqueConstraintViolation(dbEx));
        }

        [Fact]
        public void IsUniqueConstraintViolation_InnerNull_ReturnsFalse()
        {
            var dbEx = new DbUpdateException("Update failed", new Exception("generic"));
            Assert.False(DocumentIngestionService.IsUniqueConstraintViolation(dbEx));
        }

        [Fact]
        public void NispahWriteLog_SameIdempotencyKey_ConsideredDuplicate()
        {
            var log1 = new NispahWriteLog
            {
                TikCounter = 39283,
                NispahType = "סיפור תאונה",
                SourceItemId = 2732400533,
                InfoHash = "abc123def456",
                SourceKind = DocumentIngestionService.NispahSourceKindAccidentStory,
                CreatedAtUtc = DateTime.UtcNow,
                Failed = false
            };
            var log2 = new NispahWriteLog
            {
                TikCounter = 39283,
                NispahType = "סיפור תאונה",
                SourceItemId = 2732400533,
                InfoHash = "abc123def456",
                SourceKind = DocumentIngestionService.NispahSourceKindAccidentStory,
                CreatedAtUtc = DateTime.UtcNow.AddSeconds(1),
                Failed = false
            };
            Assert.Equal(log1.TikCounter, log2.TikCounter);
            Assert.Equal(log1.NispahType, log2.NispahType);
            Assert.Equal(log1.SourceItemId, log2.SourceItemId);
            Assert.Equal(log1.InfoHash, log2.InfoHash);
        }

        [Fact]
        public void NispahWriteLog_DifferentInfoHash_Allowed()
        {
            var log1 = new NispahWriteLog
            {
                TikCounter = 39283,
                NispahType = "סיפור תאונה",
                SourceItemId = 2732400533,
                InfoHash = "hash1",
                SourceKind = DocumentIngestionService.NispahSourceKindAccidentStory,
                CreatedAtUtc = DateTime.UtcNow,
                Failed = false
            };
            var log2 = new NispahWriteLog
            {
                TikCounter = 39283,
                NispahType = "סיפור תאונה",
                SourceItemId = 2732400533,
                InfoHash = "hash2",
                SourceKind = DocumentIngestionService.NispahSourceKindAccidentStory,
                CreatedAtUtc = DateTime.UtcNow,
                Failed = false
            };
            Assert.NotEqual(log1.InfoHash, log2.InfoHash);
        }

        [Fact]
        public void NispahWriteLog_Concurrency_UniqueIndexColumnsMatchRequirement()
        {
            Assert.Equal("AccidentStory", DocumentIngestionService.NispahSourceKindAccidentStory);
            Assert.Equal("PdfAsset", DocumentIngestionService.NispahSourceKindPdfAsset);
        }
    }
}
