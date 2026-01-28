using System;
using System.Data;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Odmon.Worker.Models;

namespace Odmon.Worker.OdcanitAccess
{
    /// <summary>
    /// SQL-based implementation of IOdcanitWriter that appends Nispah records via Odcanit stored procedures.
    /// </summary>
    public class SqlOdcanitWriter : IOdcanitWriter
    {
        private readonly OdcanitDbContext _db;
        private readonly ILogger<SqlOdcanitWriter> _logger;

        private const string StoredProcedureName = "dbo.Klita_Interface_NispahDetails";
        private const int CommandTimeoutSeconds = 30;

        public SqlOdcanitWriter(OdcanitDbContext db, ILogger<SqlOdcanitWriter> logger)
        {
            _db = db;
            _logger = logger;
        }

        public async Task AppendNispahAsync(OdcanitCase c, DateTime nowUtc, string nispahType, string info, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(c.TikNumber))
            {
                throw new ArgumentException($"TikNumber is required but was null or empty for TikCounter={c.TikCounter}", nameof(c));
            }

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
                command.CommandTimeout = CommandTimeoutSeconds;

                // Parameters: @TikVisualID, @Info, @NispahTypeName, @Error OUTPUT
                command.Parameters.Add(new SqlParameter("@TikVisualID", SqlDbType.NVarChar, 50) { Value = c.TikNumber });
                command.Parameters.Add(new SqlParameter("@Info", SqlDbType.NVarChar, 2000) { Value = info ?? string.Empty });
                command.Parameters.Add(new SqlParameter("@NispahTypeName", SqlDbType.NVarChar, 100) { Value = nispahType ?? string.Empty });
                command.Parameters.Add(new SqlParameter("@Error", SqlDbType.NVarChar, 4000) { Direction = ParameterDirection.Output });

                await command.ExecuteNonQueryAsync(ct);

                // Read @Error OUTPUT parameter
                var errorParam = (SqlParameter)command.Parameters["@Error"];
                var errorValue = errorParam.Value as string ?? errorParam.Value?.ToString();

                if (!string.IsNullOrWhiteSpace(errorValue))
                {
                    var errorMessage = $"Stored procedure {StoredProcedureName} returned error: {errorValue}";
                    _logger.LogError(
                        "Nispah write failed: TikCounter={TikCounter}, TikNumber={TikNumber}, NispahType={NispahType}, Error={Error}",
                        c.TikCounter,
                        c.TikNumber,
                        nispahType,
                        errorValue);
                    throw new InvalidOperationException(errorMessage);
                }

                _logger.LogInformation(
                    "Nispah write succeeded: TikCounter={TikCounter}, TikNumber={TikNumber}, NispahType={NispahType}, Info='{Info}'",
                    c.TikCounter,
                    c.TikNumber,
                    nispahType,
                    info);
            }
            finally
            {
                if (wasClosed && connection.State == ConnectionState.Open)
                {
                    await connection.CloseAsync();
                }
            }
        }
    }
}

