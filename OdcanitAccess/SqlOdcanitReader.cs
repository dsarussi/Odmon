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

            var sideCounterSet = new HashSet<int>(sideCounters);
            var visualIdSet = new HashSet<string>(visualIds);

            var clientsFromDb = await _db.Clients
                .AsNoTracking()
                .ToListAsync(ct);

            var clients = clientsFromDb
                .Where(x => sideCounterSet.Contains(x.SideCounter) && visualIdSet.Contains(x.VisualID))
                .ToList();

            var clientLookup = clients
                .GroupBy(x => new { x.SideCounter, x.VisualID })
                .ToDictionary(
                    g => (g.Key.SideCounter, g.Key.VisualID),
                    g => g.First());

            foreach (var odcanitCase in cases)
            {
                if (odcanitCase.ClientVisualID is null)
                {
                    continue;
                }

                if (!clientLookup.TryGetValue((odcanitCase.SideCounter, odcanitCase.ClientVisualID), out var client))
                {
                    continue;
                }

                odcanitCase.ClientPhone = client.Mobile;
                odcanitCase.ClientEmail = client.Email;
            }

            return cases;
        }
    }
}

