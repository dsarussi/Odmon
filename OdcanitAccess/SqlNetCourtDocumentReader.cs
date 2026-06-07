using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Odmon.Worker.Models;

namespace Odmon.Worker.OdcanitAccess
{
    public class SqlNetCourtDocumentReader : INetCourtDocumentReader
    {
        private readonly OdcanitDbContext _db;
        private readonly ILogger<SqlNetCourtDocumentReader> _logger;

        public SqlNetCourtDocumentReader(
            OdcanitDbContext db,
            ILogger<SqlNetCourtDocumentReader> logger)
        {
            _db = db;
            _logger = logger;
        }

        public async Task<List<NetCourtDocument>> GetDecisionDocumentsFromDocDateAsync(
            DateTime startFromDocDate,
            CancellationToken ct)
        {
            var rows = new List<NetCourtDocument>();
            var connection = _db.Database.GetDbConnection();
            var wasOpen = connection.State == ConnectionState.Open;
            if (!wasOpen)
            {
                await connection.OpenAsync(ct);
            }

            try
            {
                await using var command = connection.CreateCommand();
                command.CommandText = @"
SELECT
    [Counter],
    [TikCounter],
    [ODDocID],
    [CourtDocumentID],
    [DocType],
    [DocDate],
    [tsCreateDate],
    [Description],
    [DecisionDesc],
    [DecisionID]
FROM [vwNetCourtDocs]
WHERE [DocType] IN (2, 3)
  AND [DocDate] >= @startFromDocDate
ORDER BY [DocDate] ASC, [Counter] ASC;";

                var startDateParameter = command.CreateParameter();
                startDateParameter.ParameterName = "@startFromDocDate";
                startDateParameter.DbType = DbType.Date;
                startDateParameter.Value = startFromDocDate.Date;
                command.Parameters.Add(startDateParameter);

                await using var reader = await command.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                {
                    rows.Add(new NetCourtDocument
                    {
                        Counter = ReadInt64(reader, 0),
                        TikCounter = ReadInt32(reader, 1),
                        ODDocID = ReadNullableInt64(reader, 2),
                        CourtDocumentID = ReadNullableInt64(reader, 3),
                        DocType = ReadInt32(reader, 4),
                        DocDate = ReadNullableDateTime(reader, 5),
                        tsCreateDate = ReadNullableDateTime(reader, 6),
                        Description = ReadNullableString(reader, 7),
                        DecisionDesc = ReadNullableString(reader, 8),
                        DecisionID = ReadNullableInt64(reader, 9)
                    });
                }
            }
            finally
            {
                if (!wasOpen)
                {
                    await connection.CloseAsync();
                }
            }

            _logger.LogInformation(
                "NETCOURT reader loaded {Count} DocType 2/3 row(s) from vwNetCourtDocs. StartFromDocDate={StartFromDocDate:yyyy-MM-dd}",
                rows.Count,
                startFromDocDate);

            return rows;
        }

        private static long ReadInt64(System.Data.Common.DbDataReader reader, int ordinal)
            => Convert.ToInt64(reader.GetValue(ordinal), System.Globalization.CultureInfo.InvariantCulture);

        private static int ReadInt32(System.Data.Common.DbDataReader reader, int ordinal)
            => Convert.ToInt32(reader.GetValue(ordinal), System.Globalization.CultureInfo.InvariantCulture);

        private static long? ReadNullableInt64(System.Data.Common.DbDataReader reader, int ordinal)
            => reader.IsDBNull(ordinal) ? null : ReadInt64(reader, ordinal);

        private static DateTime? ReadNullableDateTime(System.Data.Common.DbDataReader reader, int ordinal)
            => reader.IsDBNull(ordinal)
                ? null
                : Convert.ToDateTime(reader.GetValue(ordinal), System.Globalization.CultureInfo.InvariantCulture);

        private static string? ReadNullableString(System.Data.Common.DbDataReader reader, int ordinal)
            => reader.IsDBNull(ordinal)
                ? null
                : Convert.ToString(reader.GetValue(ordinal), System.Globalization.CultureInfo.InvariantCulture);
    }
}
