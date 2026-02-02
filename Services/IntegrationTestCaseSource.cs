using System;
using System.Collections.Generic;
using System.Data;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Odmon.Worker.Data;
using Odmon.Worker.Models;

namespace Odmon.Worker.Services
{
    /// <summary>
    /// Test-only case source that reads cases from IntegrationDb test table (e.g. dbo.TestCases1808)
    /// instead of Odcanit. Used when Testing.Enable = true.
    /// </summary>
    public class IntegrationTestCaseSource : ICaseSource
    {
        private readonly IntegrationDbContext _integrationDb;
        private readonly IConfiguration _config;
        private readonly ILogger<IntegrationTestCaseSource> _logger;

        public IntegrationTestCaseSource(
            IntegrationDbContext integrationDb,
            IConfiguration config,
            ILogger<IntegrationTestCaseSource> logger)
        {
            _integrationDb = integrationDb;
            _config = config;
            _logger = logger;
        }

        public async Task<List<OdcanitCase>> GetCasesByTikCountersAsync(IEnumerable<int> tikCounters, CancellationToken ct)
        {
            var list = tikCounters != null ? new List<int>(tikCounters) : new List<int>();
            if (list.Count == 0)
            {
                return new List<OdcanitCase>();
            }

            var testingSection = _config.GetSection("Testing");
            var tableName = testingSection.GetValue<string>("TableName") ?? "dbo.TestCases1808";

            _logger.LogInformation(
                "Reading test cases from IntegrationDb table {TableName} for TikCounters={TikCounters}",
                tableName,
                string.Join(", ", list));

            var cases = new List<OdcanitCase>();

            var connection = _integrationDb.Database.GetDbConnection();
            var wasClosed = connection.State == ConnectionState.Closed;
            if (wasClosed)
            {
                await connection.OpenAsync(ct);
            }

            try
            {
                await using var command = connection.CreateCommand();
                command.CommandText = BuildSql(tableName, list.Count);
                command.CommandType = CommandType.Text;

                for (int i = 0; i < list.Count; i++)
                {
                    var p = command.CreateParameter();
                    p.ParameterName = $"@p{i}";
                    p.DbType = DbType.Int32;
                    p.Value = list[i];
                    command.Parameters.Add(p);
                }

                await using var reader = await command.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                {
                    var raw = new Dictionary<string, string?>(StringComparer.Ordinal);
                    for (int i = 0; i < reader.FieldCount; i++)
                    {
                        var name = reader.GetName(i);
                        var value = reader.IsDBNull(i) ? null : reader.GetValue(i)?.ToString();
                        raw[name] = value;
                    }

                    var tikCounter = GetInt(raw, "TikCounter") ?? 0;
                    if (tikCounter <= 0)
                    {
                        continue;
                    }

                    var c = new OdcanitCase
                    {
                        TikCounter = tikCounter,
                        TikNumber = GetString(raw, "מספר תיק") ?? string.Empty,
                        ClientName = GetString(raw, "שם לקוח") ?? string.Empty,
                        PlaintiffPhone = GetString(raw, "טלפון - תובע"),
                        DocumentType = GetString(raw, "סוג מסמך"),
                        RawFields = raw
                    };

                    cases.Add(c);
                }
            }
            finally
            {
                if (wasClosed && connection.State == ConnectionState.Open)
                {
                    await connection.CloseAsync();
                }
            }

            _logger.LogInformation(
                "Loaded {Count} test case(s) from IntegrationDb table {TableName}.",
                cases.Count,
                tableName);

            return cases;
        }

        private static string BuildSql(string tableName, int parameterCount)
        {
            var paramNames = new string[parameterCount];
            for (int i = 0; i < parameterCount; i++)
            {
                paramNames[i] = $"@p{i}";
            }

            return $"SELECT * FROM {tableName} WHERE TikCounter IN ({string.Join(", ", paramNames)})";
        }

        private static int? GetInt(Dictionary<string, string?> raw, string key)
        {
            if (!raw.TryGetValue(key, out var s) || string.IsNullOrWhiteSpace(s))
            {
                return null;
            }
            if (int.TryParse(s, out var value))
            {
                return value;
            }
            return null;
        }

        private static string? GetString(Dictionary<string, string?> raw, string key)
        {
            return raw.TryGetValue(key, out var s) ? s : null;
        }
    }
}

