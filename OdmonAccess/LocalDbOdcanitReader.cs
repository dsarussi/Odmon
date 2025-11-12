using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Odmon.Worker.Data;
using Odmon.Worker.Models;

namespace Odmon.Worker.OdcanitAccess
{
    public class LocalDbOdcanitReader : IOdcanitReader
    {
        private readonly IntegrationDbContext _db;
        public LocalDbOdcanitReader(IntegrationDbContext db) => _db = db;

        public async Task<List<Odmon.Worker.Models.OdcanitCase>> GetCasesUpdatedSinceAsync(DateTime lastSyncUtc, CancellationToken ct)
        {
            // This method expects a table/view named OdcanitCasesMock to exist in the same DB.
            // Cursor: do NOT create SQL here. Just keep the call site ready.
            return await _db.Set<Odmon.Worker.Models.OdcanitCase>()
                .FromSqlRaw("SELECT TikCounter, TikNumber, TikName, ClientName, StatusName, TikOwner, tsCreateDate, tsModifyDate, Notes FROM dbo.OdcanitCasesMock WHERE tsModifyDate > {0}", lastSyncUtc)
                .OrderBy(c => c.tsModifyDate)
                .ToListAsync(ct);
        }
    }
}

