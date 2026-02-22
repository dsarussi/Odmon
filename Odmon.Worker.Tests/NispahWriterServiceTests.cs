using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Xunit;
using Odmon.Worker.Services;

namespace Odmon.Worker.Tests
{
    public class NispahWriterServiceTests
    {
        // ── IsMissingTableSqlException ───────────────────────────────────

        [Fact]
        public void IsMissingTableSqlException_DirectSqlException208_ReturnsTrue()
        {
            var ex = SqlExceptionTestHelper.Create(208, "Invalid object name 'NispahAuditLogs'.");
            Assert.True(NispahWriterService.IsMissingTableSqlException(ex));
        }

        [Fact]
        public void IsMissingTableSqlException_WrappedInDbUpdateException_ReturnsTrue()
        {
            var sqlEx = SqlExceptionTestHelper.Create(208, "Invalid object name 'NispahAuditLogs'.");
            var dbEx = new DbUpdateException("An error occurred while saving.", sqlEx);
            Assert.True(NispahWriterService.IsMissingTableSqlException(dbEx));
        }

        [Fact]
        public void IsMissingTableSqlException_SqlException_Non208_ReturnsFalse()
        {
            var ex = SqlExceptionTestHelper.Create(2627, "Violation of UNIQUE KEY constraint.");
            Assert.False(NispahWriterService.IsMissingTableSqlException(ex));
        }

        [Fact]
        public void IsMissingTableSqlException_DbUpdateWithNon208Inner_ReturnsFalse()
        {
            var sqlEx = SqlExceptionTestHelper.Create(547, "FK constraint violation.");
            var dbEx = new DbUpdateException("An error occurred while saving.", sqlEx);
            Assert.False(NispahWriterService.IsMissingTableSqlException(dbEx));
        }

        [Fact]
        public void IsMissingTableSqlException_PlainException_ReturnsFalse()
        {
            Assert.False(NispahWriterService.IsMissingTableSqlException(new InvalidOperationException("test")));
        }

        [Fact]
        public void IsMissingTableSqlException_DbUpdateWithNullInner_ReturnsFalse()
        {
            var dbEx = new DbUpdateException("test", new Exception("generic"));
            Assert.False(NispahWriterService.IsMissingTableSqlException(dbEx));
        }

        // ── ResetTableMissingFlags ──────────────────────────────────────

        [Fact]
        public void ResetTableMissingFlags_CanBeCalledSafely()
        {
            NispahWriterService.ResetTableMissingFlags();
        }

        // ── IsDuplicateKeySqlError (idempotent dedup: 2601/2627 → skip) ───

        [Fact]
        public void IsDuplicateKeySqlError_2601_ReturnsTrue()
        {
            Assert.True(NispahWriterService.IsDuplicateKeySqlError(2601));
        }

        [Fact]
        public void IsDuplicateKeySqlError_2627_ReturnsTrue()
        {
            Assert.True(NispahWriterService.IsDuplicateKeySqlError(2627));
        }

        [Fact]
        public void IsDuplicateKeySqlError_OtherNumbers_ReturnsFalse()
        {
            Assert.False(NispahWriterService.IsDuplicateKeySqlError(208));
            Assert.False(NispahWriterService.IsDuplicateKeySqlError(547));
            Assert.False(NispahWriterService.IsDuplicateKeySqlError(0));
        }
    }
}
