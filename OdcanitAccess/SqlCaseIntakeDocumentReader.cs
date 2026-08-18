using System.Data;
using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Odmon.Worker.Models;

namespace Odmon.Worker.OdcanitAccess
{
    public sealed class SqlCaseIntakeDocumentReader : ICaseIntakeDocumentReader
    {
        internal const string RelevantDocumentsCommandText = """
SELECT
    [ID],
    [Name],
    [Path],
    [TikCounter],
    [TikVisualID],
    [DocType],
    [DocTypeDesc],
    [CreateDate]
FROM [dbo].[vwExportToOuterSystems_Documents]
WHERE [TikCounter] = @tikCounter
  AND [Name] IN (@notificationFormName, @companyDemandName, @privateDemandName)
ORDER BY [CreateDate] DESC, [ID] DESC;
""";

        private readonly OdcanitDbContext _db;
        private readonly ILogger<SqlCaseIntakeDocumentReader> _logger;

        public SqlCaseIntakeDocumentReader(
            OdcanitDbContext db,
            ILogger<SqlCaseIntakeDocumentReader> logger)
        {
            _db = db;
            _logger = logger;
        }

        public async Task<IReadOnlyList<OdcanitCaseDocument>> GetRelevantDocumentsAsync(
            int tikCounter,
            CancellationToken ct)
        {
            if (tikCounter <= 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(tikCounter),
                    tikCounter,
                    "TikCounter must be a positive integer.");
            }

            var documents = new List<OdcanitCaseDocument>();
            var connection = _db.Database.GetDbConnection();
            var wasOpen = connection.State == ConnectionState.Open;
            if (!wasOpen)
            {
                await connection.OpenAsync(ct);
            }

            try
            {
                await using var command = connection.CreateCommand();
                command.CommandText = RelevantDocumentsCommandText;
                AddParameter(command, "@tikCounter", DbType.Int32, tikCounter);
                AddParameter(
                    command,
                    "@notificationFormName",
                    DbType.String,
                    CaseIntakeDocumentClassifier.DigitalNotificationFormName);
                AddParameter(
                    command,
                    "@companyDemandName",
                    DbType.String,
                    CaseIntakeDocumentClassifier.CompanyDemandLetterName);
                AddParameter(
                    command,
                    "@privateDemandName",
                    DbType.String,
                    CaseIntakeDocumentClassifier.PrivatePartyDemandLetterName);

                await using var reader = await command.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                {
                    documents.Add(new OdcanitCaseDocument(
                        Id: ReadInt64(reader, "ID"),
                        Name: ReadRequiredString(reader, "Name"),
                        Path: ReadNullableString(reader, "Path"),
                        TikCounter: ReadInt32(reader, "TikCounter"),
                        TikVisualId: ReadNullableString(reader, "TikVisualID"),
                        DocType: ReadNullableInvariantString(reader, "DocType"),
                        DocTypeDescription: ReadNullableString(reader, "DocTypeDesc"),
                        CreateDate: ReadNullableDateTime(reader, "CreateDate")));
                }
            }
            finally
            {
                if (!wasOpen && connection.State == ConnectionState.Open)
                {
                    await connection.CloseAsync();
                }
            }

            _logger.LogInformation(
                "CASE_INTAKE_READ | Discovered {DocumentCount} relevant document(s) for TikCounter={TikCounter}",
                documents.Count,
                tikCounter);

            return documents;
        }

        private static void AddParameter(
            DbCommand command,
            string name,
            DbType type,
            object value)
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = name;
            parameter.DbType = type;
            parameter.Value = value;
            command.Parameters.Add(parameter);
        }

        private static int ReadInt32(DbDataReader reader, string name)
            => Convert.ToInt32(
                reader.GetValue(reader.GetOrdinal(name)),
                System.Globalization.CultureInfo.InvariantCulture);

        private static long ReadInt64(DbDataReader reader, string name)
            => Convert.ToInt64(
                reader.GetValue(reader.GetOrdinal(name)),
                System.Globalization.CultureInfo.InvariantCulture);

        private static string ReadRequiredString(DbDataReader reader, string name)
        {
            var value = ReadNullableString(reader, name);
            if (string.IsNullOrEmpty(value))
            {
                throw new InvalidOperationException(
                    $"Odcanit document column '{name}' was null or empty.");
            }

            return value;
        }

        private static string? ReadNullableString(DbDataReader reader, string name)
        {
            var ordinal = reader.GetOrdinal(name);
            return reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
        }

        private static string? ReadNullableInvariantString(DbDataReader reader, string name)
        {
            var ordinal = reader.GetOrdinal(name);
            return reader.IsDBNull(ordinal)
                ? null
                : Convert.ToString(
                    reader.GetValue(ordinal),
                    System.Globalization.CultureInfo.InvariantCulture);
        }

        private static DateTime? ReadNullableDateTime(DbDataReader reader, string name)
        {
            var ordinal = reader.GetOrdinal(name);
            return reader.IsDBNull(ordinal)
                ? null
                : Convert.ToDateTime(
                    reader.GetValue(ordinal),
                    System.Globalization.CultureInfo.InvariantCulture);
        }
    }
}
