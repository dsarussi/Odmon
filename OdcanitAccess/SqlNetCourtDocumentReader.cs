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

        public async Task<long> GetMaxDecisionCounterAsync(CancellationToken ct)
        {
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
SELECT COALESCE(MAX([Counter]), 0)
FROM [vwNetCourtDocs]
WHERE [DocType] IN (2, 3);";

                var value = await command.ExecuteScalarAsync(ct);
                var maxCounter = value is null or DBNull
                    ? 0L
                    : Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture);

                _logger.LogInformation(
                    "NETCOURT reader loaded current DocType 2/3 high watermark. MaxCounter={MaxCounter}",
                    maxCounter);
                return maxCounter;
            }
            finally
            {
                if (!wasOpen)
                {
                    await connection.CloseAsync();
                }
            }
        }

        public async Task<List<NetCourtDocument>> GetDecisionDocumentsAfterCounterAsync(
            long lastSeenCounter,
            int maxBatchSize,
            CancellationToken ct)
        {
            if (maxBatchSize <= 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(maxBatchSize),
                    "Max batch size must be greater than zero.");
            }

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
SELECT TOP (@maxBatchSize)
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
  AND [Counter] > @lastSeenCounter
ORDER BY [Counter] ASC;";

                var batchParameter = command.CreateParameter();
                batchParameter.ParameterName = "@maxBatchSize";
                batchParameter.DbType = DbType.Int32;
                batchParameter.Value = maxBatchSize;
                command.Parameters.Add(batchParameter);

                var counterParameter = command.CreateParameter();
                counterParameter.ParameterName = "@lastSeenCounter";
                counterParameter.DbType = DbType.Int64;
                counterParameter.Value = lastSeenCounter;
                command.Parameters.Add(counterParameter);

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
                "NETCOURT reader loaded {Count} DocType 2/3 row(s) from vwNetCourtDocs. LastSeenCounter={LastSeenCounter}, MaxBatchSize={MaxBatchSize}",
                rows.Count,
                lastSeenCounter,
                maxBatchSize);

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
