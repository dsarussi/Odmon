using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
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

        public async Task<List<OdcanitCase>> GetCasesCreatedOnDateAsync(DateTime date, CancellationToken ct)
        {
            var startDate = date.Date;
            var endDate = startDate.AddDays(1);

            return await _db.Cases
                .AsNoTracking()
                .Where(c => c.tsCreateDate >= startDate && c.tsCreateDate < endDate)
                .OrderBy(c => c.tsCreateDate)
                .ToListAsync(ct);
        }
    }
}

