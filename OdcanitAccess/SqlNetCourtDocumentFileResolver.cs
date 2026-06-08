using System.Data;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Odmon.Worker.Configuration;

namespace Odmon.Worker.OdcanitAccess
{
    public sealed class SqlNetCourtDocumentFileResolver : INetCourtDocumentFileResolver
    {
        private const string StoredProcedureName = "dbo.procDocumentsGroup_BuildDocPath";
        private const int FileReadRetryDelayMilliseconds = 300;
        internal const string PdfDocumentExtension = ".pdf";

        private readonly OdcanitDbContext _db;
        private readonly NetCourtDecisionAlertSettings _settings;
        private readonly ILogger<SqlNetCourtDocumentFileResolver> _logger;

        public SqlNetCourtDocumentFileResolver(
            OdcanitDbContext db,
            IOptions<NetCourtDecisionAlertSettings> settings,
            ILogger<SqlNetCourtDocumentFileResolver> logger)
        {
            _db = db;
            _settings = settings.Value;
            _logger = logger;
        }

        public async Task<NetCourtDocumentFileResult> ResolveAsync(
            long? odDocId,
            string? tikNumber,
            CancellationToken ct)
        {
            if (!odDocId.HasValue)
            {
                _logger.LogWarning(
                    "NETCOURT attachment skipped because ODDocID is null. TikNumber={TikNumber}",
                    tikNumber);
                return NetCourtDocumentFileResult.Unavailable("ODDocID is null.");
            }

            _logger.LogDebug(
                "NETCOURT attachment resolution attempted. ODDocID={ODDocID}, TikNumber={TikNumber}",
                odDocId,
                tikNumber);

            string? resolvedPath;
            try
            {
                resolvedPath = await ResolvePathFromOdcanitAsync(odDocId.Value, ct);
            }
            catch (Exception ex) when (ex is SqlException or InvalidOperationException)
            {
                _logger.LogWarning(
                    ex,
                    "NETCOURT attachment path resolution failed. ODDocID={ODDocID}, TikNumber={TikNumber}, Reason={Reason}",
                    odDocId,
                    tikNumber,
                    ex.Message);
                return NetCourtDocumentFileResult.Unavailable("Odcanit path resolution failed.");
            }

            if (string.IsNullOrWhiteSpace(resolvedPath))
            {
                _logger.LogWarning(
                    "NETCOURT attachment skipped because resolved path is empty. ODDocID={ODDocID}, TikNumber={TikNumber}",
                    odDocId,
                    tikNumber);
                return NetCourtDocumentFileResult.Unavailable("Resolved path is empty.");
            }

            _logger.LogDebug(
                "NETCOURT attachment path resolved. ODDocID={ODDocID}, TikNumber={TikNumber}, FilePath={FilePath}",
                odDocId,
                tikNumber,
                resolvedPath);

            return await ValidateResolvedPathAsync(resolvedPath, odDocId.Value, tikNumber, ct);
        }

        internal async Task<NetCourtDocumentFileResult> ValidateResolvedPathAsync(
            string resolvedPath,
            long odDocId,
            string? tikNumber,
            CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(resolvedPath))
            {
                _logger.LogWarning(
                    "NETCOURT attachment skipped because resolved path is empty. ODDocID={ODDocID}, TikNumber={TikNumber}",
                    odDocId,
                    tikNumber);
                return NetCourtDocumentFileResult.Unavailable("Resolved path is empty.");
            }

            if (!IsPathUnderAllowedRoot(resolvedPath, _settings.AttachmentAllowedRoots))
            {
                _logger.LogWarning(
                    "NETCOURT attachment skipped because path is outside allowed roots. ODDocID={ODDocID}, TikNumber={TikNumber}",
                    odDocId,
                    tikNumber);
                return NetCourtDocumentFileResult.Unavailable("Resolved path is outside allowed roots.");
            }

            if (!string.Equals(Path.GetExtension(resolvedPath), ".pdf", StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogWarning(
                    "NETCOURT attachment skipped because extension is not PDF. ODDocID={ODDocID}, TikNumber={TikNumber}",
                    odDocId,
                    tikNumber);
                return NetCourtDocumentFileResult.Unavailable("Resolved file extension is not .pdf.");
            }

            try
            {
                var fileInfo = new FileInfo(resolvedPath);
                if (!fileInfo.Exists)
                {
                    _logger.LogWarning(
                        "NETCOURT attachment skipped because file does not exist. ODDocID={ODDocID}, TikNumber={TikNumber}",
                        odDocId,
                        tikNumber);
                    return new(resolvedPath, Path.GetFileName(resolvedPath), false, null, "File does not exist.");
                }

                var maxAttachmentBytes = Math.Max(0, _settings.MaxAttachmentBytes);
                if (maxAttachmentBytes > 0 && fileInfo.Length > maxAttachmentBytes)
                {
                    _logger.LogWarning(
                        "NETCOURT attachment skipped because file is too large. ODDocID={ODDocID}, TikNumber={TikNumber}, FileSizeBytes={FileSizeBytes}, MaxAttachmentBytes={MaxAttachmentBytes}",
                        odDocId,
                        tikNumber,
                        fileInfo.Length,
                        maxAttachmentBytes);
                    return new(
                        resolvedPath,
                        fileInfo.Name,
                        true,
                        fileInfo.Length,
                        "File exceeds the configured attachment size limit.");
                }

                var isPdf = await HasPdfMagicBytesWithSingleRetryAsync(resolvedPath, ct);
                if (!isPdf)
                {
                    _logger.LogWarning(
                        "NETCOURT attachment skipped because file is not a valid PDF. ODDocID={ODDocID}, TikNumber={TikNumber}",
                        odDocId,
                        tikNumber);
                    return new(resolvedPath, fileInfo.Name, true, fileInfo.Length, "File does not start with PDF magic bytes.");
                }

                return new(resolvedPath, fileInfo.Name, true, fileInfo.Length, null);
            }
            catch (UnauthorizedAccessException)
            {
                _logger.LogWarning(
                    "NETCOURT attachment skipped because access was denied. ODDocID={ODDocID}, TikNumber={TikNumber}",
                    odDocId,
                    tikNumber);
                return new(resolvedPath, Path.GetFileName(resolvedPath), false, null, "Access denied.");
            }
            catch (IOException ex)
            {
                _logger.LogWarning(
                    "NETCOURT attachment skipped because file access failed. ODDocID={ODDocID}, TikNumber={TikNumber}, Reason={Reason}",
                    odDocId,
                    tikNumber,
                    ex.Message);
                return new(resolvedPath, Path.GetFileName(resolvedPath), false, null, "File access failed.");
            }
        }

        private async Task<string?> ResolvePathFromOdcanitAsync(long odDocId, CancellationToken ct)
        {
            var connection = _db.Database.GetDbConnection();
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
                command.CommandTimeout = 15;
                command.Parameters.Add(new SqlParameter("@DocCounter", SqlDbType.BigInt)
                {
                    Value = odDocId
                });
                command.Parameters.Add(new SqlParameter("@DocExtension", SqlDbType.NVarChar, 16)
                {
                    Value = PdfDocumentExtension
                });
                var pathParameter = new SqlParameter("@ProtectedDocPath", SqlDbType.NVarChar, -1)
                {
                    Direction = ParameterDirection.Output
                };
                command.Parameters.Add(pathParameter);

                await command.ExecuteNonQueryAsync(ct);
                return pathParameter.Value is DBNull or null
                    ? null
                    : Convert.ToString(pathParameter.Value, System.Globalization.CultureInfo.InvariantCulture);
            }
            finally
            {
                if (wasClosed && connection.State == ConnectionState.Open)
                {
                    await connection.CloseAsync();
                }
            }
        }

        private static async Task<bool> HasPdfMagicBytesWithSingleRetryAsync(
            string filePath,
            CancellationToken ct)
        {
            try
            {
                return await HasPdfMagicBytesAsync(filePath, ct);
            }
            catch (IOException)
            {
                await Task.Delay(FileReadRetryDelayMilliseconds, ct);
                return await HasPdfMagicBytesAsync(filePath, ct);
            }
        }

        private static async Task<bool> HasPdfMagicBytesAsync(string filePath, CancellationToken ct)
        {
            await using var stream = new FileStream(
                filePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 4096,
                useAsync: true);
            var header = new byte[4];
            var read = await stream.ReadAsync(header, ct);
            return read == header.Length &&
                   header[0] == 0x25 &&
                   header[1] == 0x50 &&
                   header[2] == 0x44 &&
                   header[3] == 0x46;
        }

        internal static bool IsPathUnderAllowedRoot(
            string candidatePath,
            IEnumerable<string> allowedRoots)
        {
            if (string.IsNullOrWhiteSpace(candidatePath))
            {
                return false;
            }

            string fullCandidate;
            try
            {
                fullCandidate = Path.GetFullPath(candidatePath);
            }
            catch
            {
                return false;
            }

            foreach (var configuredRoot in allowedRoots)
            {
                if (string.IsNullOrWhiteSpace(configuredRoot))
                {
                    continue;
                }

                try
                {
                    var fullRoot = Path.GetFullPath(configuredRoot)
                        .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                        + Path.DirectorySeparatorChar;
                    if (fullCandidate.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }
                catch
                {
                    // Invalid configured roots are ignored.
                }
            }

            return false;
        }
    }
}
