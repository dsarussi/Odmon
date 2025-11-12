using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Odmon.Worker.Models;

namespace Odmon.Worker.OdcanitAccess
{
    public class SqlOdcanitChangeFeed : IOdcanitChangeFeed
    {
        private readonly OdcanitDbContext _odcanitDb;

        public SqlOdcanitChangeFeed(OdcanitDbContext odcanitDb)
        {
            _odcanitDb = odcanitDb;
        }

        public async Task<IReadOnlyList<int>> GetChangedTikCountersSinceAsync(DateTime sinceUtc, CancellationToken ct)
        {
            var results = new List<int>();
            var connection = _odcanitDb.Database.GetDbConnection();
            if (connection.State != ConnectionState.Open)
            {
                await connection.OpenAsync(ct);
            }

            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT DISTINCT TikCounter FROM vwExportToOuterSystems_ActionLog WHERE tsModifyDate > @sinceUtc";

            var parameter = command.CreateParameter();
            parameter.ParameterName = "@sinceUtc";
            parameter.DbType = DbType.DateTime2;
            parameter.Value = sinceUtc;
            command.Parameters.Add(parameter);

            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                if (!reader.IsDBNull(0))
                {
                    results.Add(reader.GetInt32(0));
                }
            }

            return results;
        }

        public async Task<List<OdcanitCase>> GetCasesByTikCountersAsync(IEnumerable<int> tikCounters, CancellationToken ct)
        {
            var ids = tikCounters?
                .Distinct()
                .ToList() ?? new List<int>();

            if (ids.Count == 0)
            {
                return new List<OdcanitCase>();
            }

            return await _odcanitDb.Cases
                .AsNoTracking()
                .Where(c => ids.Contains(c.TikCounter))
                .ToListAsync(ct);
        }
    }
}

