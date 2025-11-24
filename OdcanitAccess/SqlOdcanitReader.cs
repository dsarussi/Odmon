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

            var cases = await _db.Cases
                .AsNoTracking()
                .Where(c => c.tsCreateDate >= startDate && c.tsCreateDate < endDate)
                .OrderBy(c => c.tsCreateDate)
                .ToListAsync(ct);

            if (!cases.Any())
            {
                return cases;
            }

            var sideCounters = cases
                .Select(c => c.SideCounter)
                .Distinct()
                .ToList();

            var visualIds = cases
                .Select(c => c.ClientVisualID)
                .Where(id => !string.IsNullOrEmpty(id))
                .Select(id => id!)
                .Distinct()
                .ToList();

            var clients = await _db.Clients
                .AsNoTracking()
                .Where(x => sideCounters.Contains(x.SideCounter) && visualIds.Contains(x.VisualID))
                .ToListAsync(ct);

            foreach (var odcanitCase in cases)
            {
                var client = clients.FirstOrDefault(x =>
                    x.SideCounter == odcanitCase.SideCounter &&
                    x.VisualID == odcanitCase.ClientVisualID);

                if (client != null)
                {
                    odcanitCase.ClientPhone = client.Mobile;
                    odcanitCase.ClientEmail = client.Email;
                }
            }

            return cases;
        }
    }
}

