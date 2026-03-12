using System.Data;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Odmon.Worker.Configuration;
using Odmon.Worker.OdcanitAccess;

namespace Odmon.Worker.Services
{
    public class OdcanitDocumentWriter
    {
        private readonly OdcanitDbContext _odcanitDb;
        private readonly OdcanitDocumentSettings _settings;
        private readonly DocumentIngestionSettings _ingestionSettings;
        private readonly ILogger<OdcanitDocumentWriter> _logger;

        private const string SpName = "dbo.ProcDocuments_AddNewDocument";

        public OdcanitDocumentWriter(
            OdcanitDbContext odcanitDb,
            IOptions<OdcanitDocumentSettings> settings,
            IOptions<DocumentIngestionSettings> ingestionSettings,
            ILogger<OdcanitDocumentWriter> logger)
        {
            _odcanitDb = odcanitDb;
            _settings = settings.Value;
            _ingestionSettings = ingestionSettings.Value;
            _logger = logger;
        }

        public record DocumentCreateResult(int DocCounter, string DestPath);

        /// <summary>
        /// Calls ProcDocuments_AddNewDocument with MoveFile=3 to create the Documents row
        /// without triggering xp_cmdshell. The SP returns a single-row result set with
        /// DestPath and DocCounter columns. Application copies the file afterwards.
        /// </summary>
        public async Task<DocumentCreateResult> CreateDocumentRowAsync(
            int tikCounter,
            string fileName,
            string sourceFilePath,
            CancellationToken ct)
        {
            var connection = _odcanitDb.Database.GetDbConnection();
            var wasClosed = connection.State == ConnectionState.Closed;

            if (wasClosed)
                await connection.OpenAsync(ct);

            try
            {
                await using var command = (SqlCommand)connection.CreateCommand();
                command.CommandText = SpName;
                command.CommandType = CommandType.StoredProcedure;
                command.CommandTimeout = _ingestionSettings.CommandTimeoutSeconds;

                var metapelStr = _settings.Metapel.ToString();
                var summary = $"ODMON import: {fileName}";

                command.Parameters.Add(new SqlParameter("@TikCounter", SqlDbType.Int) { Value = tikCounter });
                command.Parameters.Add(new SqlParameter("@Name", SqlDbType.NVarChar, 200) { Value = fileName });
                command.Parameters.Add(new SqlParameter("@summary", SqlDbType.VarChar, 5000) { Value = summary });
                command.Parameters.Add(new SqlParameter("@CategoryCounter", SqlDbType.Int) { Value = _settings.CategoryCounter });
                command.Parameters.Add(new SqlParameter("@SubCategoryCounter", SqlDbType.Int) { Value = _settings.SubCategoryCounter });
                command.Parameters.Add(new SqlParameter("@DocStatus", SqlDbType.Int) { Value = _settings.DocStatus });
                command.Parameters.Add(new SqlParameter("@CreateDate", SqlDbType.DateTime) { Value = DateTime.Now });
                command.Parameters.Add(new SqlParameter("@WriterID", SqlDbType.Int) { Value = _settings.WriterCounter });
                command.Parameters.Add(new SqlParameter("@OwnerID", SqlDbType.Int) { Value = _settings.OwnerCounter });
                command.Parameters.Add(new SqlParameter("@DocType", SqlDbType.Int) { Value = _settings.DocType });
                command.Parameters.Add(new SqlParameter("@Metapel", SqlDbType.VarChar, -1) { Value = metapelStr });
                command.Parameters.Add(new SqlParameter("@FilePath", SqlDbType.VarChar, -1) { Value = sourceFilePath });
                command.Parameters.Add(new SqlParameter("@MoveFile", SqlDbType.TinyInt) { Value = (byte)3 });

                await using var reader = await command.ExecuteReaderAsync(ct);

                if (!await reader.ReadAsync(ct))
                {
                    throw new InvalidOperationException(
                        $"{SpName} returned no result set for TikCounter={tikCounter}, Name={fileName}. " +
                        "The SP may have failed silently.");
                }

                var destPathOrd = reader.GetOrdinal("DestPath");
                var docCounterOrd = reader.GetOrdinal("DocCounter");

                var destPath = reader.IsDBNull(destPathOrd) ? null : reader.GetString(destPathOrd);
                var docCounter = reader.IsDBNull(docCounterOrd) ? 0 : reader.GetInt32(docCounterOrd);

                if (string.IsNullOrWhiteSpace(destPath))
                {
                    throw new InvalidOperationException(
                        $"{SpName} returned empty DestPath for TikCounter={tikCounter}, Name={fileName}, DocCounter={docCounter}");
                }

                if (docCounter <= 0)
                {
                    throw new InvalidOperationException(
                        $"{SpName} returned invalid DocCounter={docCounter} for TikCounter={tikCounter}, Name={fileName}, DestPath='{destPath}'");
                }

                _logger.LogDebug(
                    "SP {SpName} succeeded: TikCounter={TikCounter}, Name={FileName}, DocCounter={DocCounter}, DestPath={DestPath}",
                    SpName, tikCounter, fileName, docCounter, destPath);

                return new DocumentCreateResult(docCounter, destPath);
            }
            finally
            {
                if (wasClosed && connection.State == ConnectionState.Open)
                    await connection.CloseAsync();
            }
        }

        /// <summary>
        /// Resolves SideCounter (client number) from TikCounter using vwExportToOuterSystems_Files.
        /// </summary>
        public async Task<int?> ResolveSideCounterAsync(int tikCounter, CancellationToken ct)
        {
            var connection = _odcanitDb.Database.GetDbConnection();
            var wasClosed = connection.State == ConnectionState.Closed;

            if (wasClosed)
                await connection.OpenAsync(ct);

            try
            {
                await using var command = (SqlCommand)connection.CreateCommand();
                command.CommandText = "SELECT TOP 1 SideCounter FROM dbo.vwExportToOuterSystems_Files WHERE TikCounter = @TikCounter";
                command.CommandType = CommandType.Text;
                command.CommandTimeout = 15;
                command.Parameters.Add(new SqlParameter("@TikCounter", SqlDbType.Int) { Value = tikCounter });

                var result = await command.ExecuteScalarAsync(ct);
                if (result is int value)
                    return value;
                if (result is DBNull || result is null)
                    return null;
                if (int.TryParse(result.ToString(), out var parsed))
                    return parsed;

                return null;
            }
            finally
            {
                if (wasClosed && connection.State == ConnectionState.Open)
                    await connection.CloseAsync();
            }
        }

        /// <summary>
        /// Resolves TikCounter from TikVisualID using dbo.MainTik.
        /// Tries column "TikCounter" first; if that column doesn't exist (SqlException 207)
        /// or returns null, falls back to column "Counter" for compatibility across DB versions.
        /// </summary>
        public async Task<int?> ResolveTikCounterAsync(string tikVisualID, CancellationToken ct)
        {
            var connection = _odcanitDb.Database.GetDbConnection();
            var wasClosed = connection.State == ConnectionState.Closed;

            if (wasClosed)
                await connection.OpenAsync(ct);

            try
            {
                var result = await TryQueryMainTikColumnAsync(
                    connection, "TikCounter", tikVisualID, ct);

                if (result.HasValue)
                {
                    _logger.LogDebug(
                        "Resolved TikVisualID={TikVisualID} → TikCounter={TikCounter} via column 'TikCounter'",
                        tikVisualID, result.Value);
                    return result.Value;
                }

                result = await TryQueryMainTikColumnAsync(
                    connection, "Counter", tikVisualID, ct);

                if (result.HasValue)
                {
                    _logger.LogDebug(
                        "Resolved TikVisualID={TikVisualID} → TikCounter={TikCounter} via fallback column 'Counter'",
                        tikVisualID, result.Value);
                    return result.Value;
                }

                _logger.LogWarning(
                    "TikCounter not found in dbo.MainTik for TikVisualID={TikVisualID} (tried columns 'TikCounter' and 'Counter')",
                    tikVisualID);
                return null;
            }
            finally
            {
                if (wasClosed && connection.State == ConnectionState.Open)
                    await connection.CloseAsync();
            }
        }

        private async Task<int?> TryQueryMainTikColumnAsync(
            System.Data.Common.DbConnection connection, string columnName, string tikVisualID, CancellationToken ct)
        {
            try
            {
                await using var command = (SqlCommand)connection.CreateCommand();
                command.CommandText = $"SELECT TOP 1 [{columnName}] FROM dbo.MainTik WHERE VisualID = @TikVisualID";
                command.CommandType = CommandType.Text;
                command.CommandTimeout = 15;
                command.Parameters.Add(new SqlParameter("@TikVisualID", SqlDbType.NVarChar, 50) { Value = tikVisualID });

                var result = await command.ExecuteScalarAsync(ct);
                if (result is int value)
                    return value;
                if (result is DBNull || result is null)
                    return null;
                if (int.TryParse(result.ToString(), out var parsed))
                    return parsed;

                return null;
            }
            catch (SqlException ex) when (ex.Number == 207)
            {
                _logger.LogDebug(
                    "Column '{ColumnName}' does not exist in dbo.MainTik (SqlException 207), will try fallback",
                    columnName);
                return null;
            }
        }
    }
}
