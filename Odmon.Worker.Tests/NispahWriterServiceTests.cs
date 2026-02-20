using System.Reflection;
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
            var ex = CreateSqlException(208, "Invalid object name 'NispahAuditLogs'.");
            Assert.True(NispahWriterService.IsMissingTableSqlException(ex));
        }

        [Fact]
        public void IsMissingTableSqlException_WrappedInDbUpdateException_ReturnsTrue()
        {
            var sqlEx = CreateSqlException(208, "Invalid object name 'NispahAuditLogs'.");
            var dbEx = new DbUpdateException("An error occurred while saving.", sqlEx);
            Assert.True(NispahWriterService.IsMissingTableSqlException(dbEx));
        }

        [Fact]
        public void IsMissingTableSqlException_SqlException_Non208_ReturnsFalse()
        {
            var ex = CreateSqlException(2627, "Violation of UNIQUE KEY constraint.");
            Assert.False(NispahWriterService.IsMissingTableSqlException(ex));
        }

        [Fact]
        public void IsMissingTableSqlException_DbUpdateWithNon208Inner_ReturnsFalse()
        {
            var sqlEx = CreateSqlException(547, "FK constraint violation.");
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

        // ── Helper: create SqlException via reflection ──────────────────

        private static SqlException CreateSqlException(int number, string message)
        {
            var errorCtor = typeof(SqlError).GetConstructors(BindingFlags.NonPublic | BindingFlags.Instance)
                .OrderByDescending(c => c.GetParameters().Length)
                .First();

            var paramCount = errorCtor.GetParameters().Length;

            // Microsoft.Data.SqlClient SqlError constructors vary by version;
            // build args dynamically based on parameter count.
            var args = new object?[paramCount];
            var paramInfos = errorCtor.GetParameters();

            for (int i = 0; i < paramCount; i++)
            {
                var p = paramInfos[i];
                args[i] = p.Name switch
                {
                    "infoNumber" => number,
                    "errorState" => (byte)1,
                    "errorClass" => (byte)17,
                    "server" => "test-server",
                    "errorMessage" or "message" => message,
                    "procedure" => "test-proc",
                    "lineNumber" => 1,
                    "win32ErrorCode" => (uint)0,
                    _ => p.ParameterType.IsValueType
                        ? Activator.CreateInstance(p.ParameterType)
                        : null
                };
            }

            var error = (SqlError)errorCtor.Invoke(args);

            var collection = (SqlErrorCollection)Activator.CreateInstance(
                typeof(SqlErrorCollection),
                BindingFlags.NonPublic | BindingFlags.Instance, null, null, null)!;

            typeof(SqlErrorCollection)
                .GetMethod("Add", BindingFlags.NonPublic | BindingFlags.Instance)!
                .Invoke(collection, new object[] { error });

            // Try the static factory first, fall back to constructor
            var factory = typeof(SqlException).GetMethod(
                "CreateException",
                BindingFlags.NonPublic | BindingFlags.Static,
                null,
                new[] { typeof(SqlErrorCollection), typeof(string) },
                null);

            if (factory != null)
                return (SqlException)factory.Invoke(null, new object[] { collection, "16.0" })!;

            // Fallback: try instance constructor
            var ctor = typeof(SqlException).GetConstructors(BindingFlags.NonPublic | BindingFlags.Instance)
                .FirstOrDefault();

            if (ctor != null)
            {
                var ctorParams = ctor.GetParameters();
                var ctorArgs = new object?[ctorParams.Length];
                for (int i = 0; i < ctorParams.Length; i++)
                {
                    var p = ctorParams[i];
                    if (p.ParameterType == typeof(string)) ctorArgs[i] = message;
                    else if (p.ParameterType == typeof(SqlErrorCollection)) ctorArgs[i] = collection;
                    else if (p.ParameterType == typeof(Guid)) ctorArgs[i] = Guid.Empty;
                    else ctorArgs[i] = p.ParameterType.IsValueType
                        ? Activator.CreateInstance(p.ParameterType) : null;
                }
                return (SqlException)ctor.Invoke(ctorArgs)!;
            }

            throw new InvalidOperationException("Cannot create SqlException via reflection — internal API changed.");
        }
    }
}
