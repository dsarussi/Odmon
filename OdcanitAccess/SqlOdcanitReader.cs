using Microsoft.EntityFrameworkCore;
using Odmon.Worker.Models;

namespace Odmon.Worker.OdcanitAccess
{
    public class SqlOdcanitReader : IOdcanitReader
    {
        private readonly OdcanitDbContext _db;

        public SqlOdcanitReader(OdcanitDbContext db)
        {
            _db = db;
        }

        public async Task<List<OdcanitCase>> GetCasesUpdatedSinceAsync(DateTime lastSyncUtc, CancellationToken ct)
        {
            return await _db.Cases
                .Where(c => c.tsModifyDate > lastSyncUtc)
                .ToListAsync(ct);
        }
    }
}

