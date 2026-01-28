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

        // TODO: Replace with actual Odcanit Nispah proc name & parameters from Odcanit documentation.
        private const string AddNispahProcName = "dbo.Odmon_AddNispah";

        public SqlOdcanitWriter(OdcanitDbContext db, ILogger<SqlOdcanitWriter> logger)
        {
            _db = db;
            _logger = logger;
        }

        public async Task AppendNispahAsync(OdcanitCase c, DateTime nowUtc, string nispahType, string info, CancellationToken ct)
        {
            var israelTimeZone = TimeZoneInfo.FindSystemTimeZoneById("Israel Standard Time");
            var nowLocal = TimeZoneInfo.ConvertTimeFromUtc(nowUtc, israelTimeZone);

            var parameters = new[]
            {
                new SqlParameter("@TikCounter", SqlDbType.Int) { Value = c.TikCounter },
                new SqlParameter("@ClientVisualID", SqlDbType.NVarChar, 50) { Value = (object?)c.ClientVisualID ?? DBNull.Value },
                new SqlParameter("@ClientName", SqlDbType.NVarChar, 200) { Value = (object?)c.ClientName ?? DBNull.Value },
                new SqlParameter("@TikNumber", SqlDbType.NVarChar, 50) { Value = (object?)c.TikNumber ?? DBNull.Value },
                new SqlParameter("@TikName", SqlDbType.NVarChar, 200) { Value = (object?)c.TikName ?? DBNull.Value },
                new SqlParameter("@NispahDate", SqlDbType.Date) { Value = nowLocal.Date },
                new SqlParameter("@NispahTime", SqlDbType.Time) { Value = nowLocal.TimeOfDay },
                new SqlParameter("@NispahType", SqlDbType.NVarChar, 100) { Value = nispahType },
                new SqlParameter("@Info", SqlDbType.NVarChar, 2000) { Value = info },
                new SqlParameter("@tsCreateDate", SqlDbType.DateTime2) { Value = nowUtc },
                new SqlParameter("@tsModifyDate", SqlDbType.DateTime2) { Value = nowUtc },
                new SqlParameter("@tsCreatedBy", SqlDbType.NVarChar, 50) { Value = "Monday" },
                new SqlParameter("@tsModifiedBy", SqlDbType.NVarChar, 50) { Value = "Monday" }
            };

            var sql = $"{AddNispahProcName} @TikCounter, @ClientVisualID, @ClientName, @TikNumber, @TikName, @NispahDate, @NispahTime, @NispahType, @Info, @tsCreateDate, @tsModifyDate, @tsCreatedBy, @tsModifiedBy";

            await _db.Database.ExecuteSqlRawAsync(sql, parameters, ct);

            _logger.LogInformation(
                "HearingApproval Nispah write: mode=live, TikCounter={TikCounter}, NispahType={NispahType}, Info='{Info}'",
                c.TikCounter,
                nispahType,
                info);
        }
    }
}

