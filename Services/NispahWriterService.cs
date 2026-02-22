using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Odmon.Worker.Data;
using Odmon.Worker.Models;
using Odmon.Worker.OdcanitAccess;

namespace Odmon.Worker.Services
{
    /// <summary>
    /// Highly controlled service for creating Odcanit Nispah records via stored procedure.
    /// Implements strict guardrails: allowlist validation, content safety, deduplication, rate limiting, and audit logging.
    /// </summary>
    public class NispahWriterService
    {
        private readonly OdcanitDbContext _odcanitDb;
        private readonly IntegrationDbContext _integrationDb;
        private readonly NispahWriterSettings _settings;
        private readonly ILogger<NispahWriterService> _logger;
        private readonly Regex _tikVisualIdRegex;
        
        // Rate limiting state (per-run)
        private int _createsInCurrentRun = 0;
        private readonly List<DateTime> _createsInLastMinute = new List<DateTime>();
        private readonly object _rateLimitLock = new object();

        private static volatile bool _nispahDedupTableMissing;
        private static volatile bool _nispahAuditTableMissing;

        private const string StoredProcedureName = "dbo.Klita_Interface_NispahDetails";

        public NispahWriterService(
            OdcanitDbContext odcanitDb,
            IntegrationDbContext integrationDb,
            IOptions<NispahWriterSettings> settings,
            ILogger<NispahWriterService> logger)
        {
            _odcanitDb = odcanitDb;
            _integrationDb = integrationDb;
            _settings = settings.Value;
            _logger = logger;
            
            try
            {
                _tikVisualIdRegex = new Regex(_settings.TikVisualIDRegex, RegexOptions.Compiled | RegexOptions.CultureInvariant);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Invalid TikVisualIDRegex pattern: {Pattern}", _settings.TikVisualIDRegex);
                throw new InvalidOperationException($"Invalid TikVisualIDRegex configuration: {_settings.TikVisualIDRegex}", ex);
            }
        }

        /// <summary>
        /// Creates a Nispah record in Odcanit after passing all guardrails.
        /// </summary>
        /// <param name="tikVisualID">The TikVisualID (must match configured regex)</param>
        /// <param name="info">The Nispah content (will be sanitized and length-checked)</param>
        /// <param name="nispahTypeName">The type name of the Nispah</param>
        /// <param name="correlationId">Optional correlation ID for tracking</param>
        /// <param name="ct">Cancellation token</param>
        /// <returns>True if creation succeeded, false if blocked by guardrails</returns>
        public async Task<bool> CreateNispahAsync(
            string tikVisualID,
            string info,
            string nispahTypeName,
            string? correlationId = null,
            CancellationToken ct = default)
        {
            correlationId ??= Guid.NewGuid().ToString("N");
            var startTime = DateTime.UtcNow;
            var infoHash = ComputeSha256Hash(info ?? string.Empty);
            var sanitizedInfo = SanitizeNullChars(info ?? string.Empty);
            var wasSanitized = sanitizedInfo != (info ?? string.Empty);

            // Initialize audit log entry
            var auditLog = new NispahAuditLog
            {
                CreatedAtUtc = startTime,
                CorrelationId = correlationId,
                TikVisualID = tikVisualID ?? string.Empty,
                NispahTypeName = nispahTypeName ?? string.Empty,
                InfoLength = sanitizedInfo.Length,
                InfoHash = infoHash,
                Status = "Failed"
            };

            try
            {
                // Guardrail 1: TikVisualID allowlist validation
                if (!ValidateTikVisualID(tikVisualID, auditLog))
                {
                    await PersistAuditLogAsync(auditLog, ct);
                    return false;
                }

                // Guardrail 2: Content safety
                if (!ValidateContentSafety(sanitizedInfo, wasSanitized, auditLog))
                {
                    await PersistAuditLogAsync(auditLog, ct);
                    return false;
                }

                // Guardrail 3: Deduplication check
                // After validation passes, we know tikVisualID and nispahTypeName are non-null
                if (await IsDuplicateAsync(tikVisualID!, nispahTypeName!, infoHash, ct))
                {
                    auditLog.Status = "DuplicateSkipped";
                    auditLog.Error = "Duplicate detected within deduplication window";
                    await PersistAuditLogAsync(auditLog, ct);
                    _logger.LogWarning(
                        "Nispah creation blocked (duplicate): CorrelationId={CorrelationId}, TikVisualID={TikVisualID}, NispahTypeName={NispahTypeName}, InfoHash={InfoHash}",
                        correlationId, tikVisualID, nispahTypeName, infoHash);
                    return false;
                }

                // Guardrail 4: Rate limiting
                if (!CheckRateLimits(auditLog))
                {
                    await PersistAuditLogAsync(auditLog, ct);
                    return false;
                }

                // All guardrails passed - execute stored procedure
                // After validation passes, we know tikVisualID and nispahTypeName are non-null
                var error = await ExecuteStoredProcedureAsync(tikVisualID!, sanitizedInfo, nispahTypeName!, ct);

                if (!string.IsNullOrWhiteSpace(error))
                {
                    auditLog.Status = "Failed";
                    auditLog.Error = error;
                    await PersistAuditLogAsync(auditLog, ct);
                    _logger.LogError(
                        "Nispah creation failed (stored procedure error): CorrelationId={CorrelationId}, TikVisualID={TikVisualID}, NispahTypeName={NispahTypeName}, Error={Error}",
                        correlationId, tikVisualID, nispahTypeName, error);
                    return false;
                }

                // Success - record deduplication entry and audit log
                auditLog.Status = "Success";
                // After validation passes, we know tikVisualID and nispahTypeName are non-null
                await RecordDeduplicationAsync(tikVisualID!, nispahTypeName!, infoHash, startTime, ct);
                await PersistAuditLogAsync(auditLog, ct);

                _logger.LogInformation(
                    "Nispah created successfully: CorrelationId={CorrelationId}, TikVisualID={TikVisualID}, NispahTypeName={NispahTypeName}, InfoLength={InfoLength}, InfoHash={InfoHash}",
                    correlationId, tikVisualID, nispahTypeName, sanitizedInfo.Length, infoHash);

                return true;
            }
            catch (Exception ex)
            {
                auditLog.Status = "Failed";
                auditLog.Error = $"Exception: {ex.Message}";
                await PersistAuditLogAsync(auditLog, ct);
                _logger.LogError(ex,
                    "Nispah creation exception: CorrelationId={CorrelationId}, TikVisualID={TikVisualID}, NispahTypeName={NispahTypeName}",
                    correlationId, tikVisualID, nispahTypeName);
                return false;
            }
        }

        /// <summary>
        /// Resets rate limiting counters (call at start of each run/batch).
        /// </summary>
        public void ResetRateLimits()
        {
            lock (_rateLimitLock)
            {
                _createsInCurrentRun = 0;
                _createsInLastMinute.Clear();
            }
        }

        private bool ValidateTikVisualID(string? tikVisualID, NispahAuditLog auditLog)
        {
            if (string.IsNullOrWhiteSpace(tikVisualID))
            {
                auditLog.Status = "Blocked";
                auditLog.Error = "TikVisualID is null or empty";
                _logger.LogWarning("Nispah creation blocked: TikVisualID is null or empty");
                return false;
            }

            if (!_tikVisualIdRegex.IsMatch(tikVisualID))
            {
                auditLog.Status = "Blocked";
                auditLog.Error = $"TikVisualID does not match regex pattern: {_settings.TikVisualIDRegex}";
                _logger.LogWarning(
                    "Nispah creation blocked: TikVisualID={TikVisualID} does not match regex pattern={Pattern}",
                    tikVisualID, _settings.TikVisualIDRegex);
                return false;
            }

            return true;
        }

        private bool ValidateContentSafety(string info, bool wasSanitized, NispahAuditLog auditLog)
        {
            if (info.Length > _settings.MaxInfoLength)
            {
                auditLog.Status = "Blocked";
                auditLog.Error = $"Info length ({info.Length}) exceeds MaxInfoLength ({_settings.MaxInfoLength})";
                _logger.LogWarning(
                    "Nispah creation blocked: Info length {InfoLength} exceeds MaxInfoLength {MaxInfoLength}",
                    info.Length, _settings.MaxInfoLength);
                return false;
            }

            if (wasSanitized)
            {
                _logger.LogInformation(
                    "Nispah info sanitized: null characters removed. Original length may have been different.");
            }

            return true;
        }

        private string SanitizeNullChars(string info)
        {
            if (string.IsNullOrEmpty(info))
                return info;

            if (info.Contains('\0'))
            {
                return info.Replace("\0", string.Empty);
            }

            return info;
        }

        private async Task<bool> IsDuplicateAsync(string tikVisualID, string nispahTypeName, string infoHash, CancellationToken ct)
        {
            if (_nispahDedupTableMissing) return false;

            try
            {
                var windowStart = DateTime.UtcNow.AddMinutes(-_settings.DeduplicationWindowMinutes);

                var existing = await _integrationDb.NispahDeduplications
                    .AsNoTracking()
                    .Where(d => d.TikVisualID == tikVisualID
                             && d.NispahTypeName == nispahTypeName
                             && d.InfoHash == infoHash
                             && d.CreatedAtUtc >= windowStart)
                    .FirstOrDefaultAsync(ct);

                return existing != null;
            }
            catch (SqlException sqlEx) when (sqlEx.Number == 208)
            {
                if (!_nispahDedupTableMissing)
                {
                    _nispahDedupTableMissing = true;
                    _logger.LogWarning(
                        "NispahDeduplications table does not exist (SqlException 208). Dedup check bypassed — ingestion will continue without dedup. Run EF migrations to create the table.");
                }
                return false;
            }
        }

        private bool CheckRateLimits(NispahAuditLog auditLog)
        {
            lock (_rateLimitLock)
            {
                // Check per-run limit
                if (_createsInCurrentRun >= _settings.MaxCreatesPerRun)
                {
                    auditLog.Status = "Blocked";
                    auditLog.Error = $"MaxCreatesPerRun ({_settings.MaxCreatesPerRun}) exceeded";
                    _logger.LogWarning(
                        "Nispah creation blocked: MaxCreatesPerRun {MaxCreatesPerRun} exceeded",
                        _settings.MaxCreatesPerRun);
                    return false;
                }

                // Clean old entries from per-minute tracking
                var oneMinuteAgo = DateTime.UtcNow.AddMinutes(-1);
                _createsInLastMinute.RemoveAll(dt => dt < oneMinuteAgo);

                // Check per-minute limit
                if (_createsInLastMinute.Count >= _settings.MaxCreatesPerMinute)
                {
                    auditLog.Status = "Blocked";
                    auditLog.Error = $"MaxCreatesPerMinute ({_settings.MaxCreatesPerMinute}) exceeded";
                    _logger.LogWarning(
                        "Nispah creation blocked: MaxCreatesPerMinute {MaxCreatesPerMinute} exceeded",
                        _settings.MaxCreatesPerMinute);
                    return false;
                }

                // Increment counters
                _createsInCurrentRun++;
                _createsInLastMinute.Add(DateTime.UtcNow);

                return true;
            }
        }

        private async Task<string?> ExecuteStoredProcedureAsync(
            string tikVisualID,
            string info,
            string nispahTypeName,
            CancellationToken ct)
        {
            var connection = _odcanitDb.Database.GetDbConnection();
            var wasClosed = connection.State == ConnectionState.Closed;
            
            if (wasClosed)
            {
                await connection.OpenAsync(ct);
            }

            try
            {
                await using var command = (SqlCommand)connection.CreateCommand();
                command.CommandText = StoredProcedureName;
                command.CommandType = CommandType.StoredProcedure;
                command.CommandTimeout = _settings.CommandTimeoutSeconds;

                // Add parameters using SqlParameter for proper SQL Server support
                command.Parameters.Add(new SqlParameter("@TikVisualID", SqlDbType.NVarChar, 50) { Value = tikVisualID });
                command.Parameters.Add(new SqlParameter("@Info", SqlDbType.NVarChar, _settings.MaxInfoLength) { Value = info });
                command.Parameters.Add(new SqlParameter("@NispahTypeName", SqlDbType.NVarChar, 100) { Value = nispahTypeName });
                command.Parameters.Add(new SqlParameter("@Error", SqlDbType.NVarChar, 4000) { Direction = ParameterDirection.Output });

                // Execute with retry on transient errors
                try
                {
                    await command.ExecuteNonQueryAsync(ct);
                }
                catch (SqlException sqlEx) when (IsTransientError(sqlEx))
                {
                    // Retry once on transient errors
                    _logger.LogWarning(
                        "Transient SQL error detected, retrying once: ErrorNumber={ErrorNumber}, Message={Message}",
                        sqlEx.Number, sqlEx.Message);
                    
                    await Task.Delay(100, ct); // Brief delay before retry
                    await command.ExecuteNonQueryAsync(ct);
                }

                // Read output parameter
                var errorParam = (SqlParameter)command.Parameters["@Error"];
                var errorValue = errorParam.Value as string ?? errorParam.Value?.ToString();
                return string.IsNullOrWhiteSpace(errorValue) ? null : errorValue;
            }
            finally
            {
                if (wasClosed && connection.State == ConnectionState.Open)
                {
                    await connection.CloseAsync();
                }
            }
        }

        private bool IsTransientError(SqlException ex)
        {
            // SQL Server error codes for deadlocks and timeouts
            // 1205 = Deadlock victim
            // -2 = Timeout expired
            return ex.Number == 1205 || ex.Number == -2;
        }

        /// <summary>
        /// Records a deduplication entry. Idempotent: if the unique key (TikVisualID, NispahTypeName, InfoHash) already exists
        /// (e.g. rescans, concurrency), treats as normal skip — no exception, no ERROR log. Run continues; counts as success.
        /// </summary>
        private async Task RecordDeduplicationAsync(
            string tikVisualID,
            string nispahTypeName,
            string infoHash,
            DateTime createdAtUtc,
            CancellationToken ct)
        {
            if (_nispahDedupTableMissing) return;

            try
            {
                var dedup = new NispahDeduplication
                {
                    CreatedAtUtc = createdAtUtc,
                    TikVisualID = tikVisualID,
                    NispahTypeName = nispahTypeName,
                    InfoHash = infoHash
                };

                _integrationDb.NispahDeduplications.Add(dedup);
                await _integrationDb.SaveChangesAsync(ct);
            }
            catch (DbUpdateException ex) when (ex.InnerException is SqlException sqlEx && IsDuplicateKeySqlError(sqlEx.Number))
            {
                // Idempotent: duplicate key (unique index UX_NispahDedup / constraint) — treat as skip, do not log exception (no stack trace).
                _logger.LogInformation(
                    "Nispah dedup exists -> skipped | TikVisualID={TikVisualID}, NispahTypeName={NispahTypeName}, InfoHash={InfoHash}",
                    tikVisualID, nispahTypeName, infoHash);
            }
            catch (DbUpdateException ex) when (ex.InnerException is SqlException sqlEx && sqlEx.Number == 208)
            {
                if (!_nispahDedupTableMissing)
                {
                    _nispahDedupTableMissing = true;
                    _logger.LogWarning(
                        "NispahDeduplications table does not exist (SqlException 208). Dedup recording skipped. Run EF migrations to create the table.");
                }
            }
        }

        /// <summary>True for SQL duplicate key errors: 2601 (unique index), 2627 (unique constraint). Concurrency-safe idempotency.</summary>
        internal static bool IsDuplicateKeySqlError(int sqlErrorNumber)
        {
            return sqlErrorNumber == 2601 || sqlErrorNumber == 2627;
        }

        internal async Task PersistAuditLogAsync(NispahAuditLog auditLog, CancellationToken ct)
        {
            if (_nispahAuditTableMissing) return;

            try
            {
                _integrationDb.NispahAuditLogs.Add(auditLog);
                await _integrationDb.SaveChangesAsync(ct);
            }
            catch (Exception ex) when (IsMissingTableSqlException(ex))
            {
                if (!_nispahAuditTableMissing)
                {
                    _nispahAuditTableMissing = true;
                    _logger.LogWarning(
                        "NispahAuditLogs table does not exist (SqlException 208). Audit logging bypassed — ingestion will continue. Run EF migrations to create the table.");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Failed to persist audit log: CorrelationId={CorrelationId}, TikVisualID={TikVisualID}",
                    auditLog.CorrelationId, auditLog.TikVisualID);
            }
        }

        /// <summary>
        /// Returns true if the exception indicates a missing SQL table (SqlException 208),
        /// whether thrown directly or wrapped in DbUpdateException.
        /// </summary>
        internal static bool IsMissingTableSqlException(Exception ex)
        {
            if (ex is SqlException { Number: 208 }) return true;
            if (ex is DbUpdateException { InnerException: SqlException { Number: 208 } }) return true;
            return false;
        }

        /// <summary>Resets static flags — only for unit tests.</summary>
        internal static void ResetTableMissingFlags()
        {
            _nispahDedupTableMissing = false;
            _nispahAuditTableMissing = false;
        }

        private static string ComputeSha256Hash(string input)
        {
            if (string.IsNullOrEmpty(input))
                return string.Empty;

            using var sha256 = SHA256.Create();
            var bytes = Encoding.UTF8.GetBytes(input);
            var hash = sha256.ComputeHash(bytes);
            return Convert.ToHexString(hash).ToLowerInvariant();
        }
    }
}
