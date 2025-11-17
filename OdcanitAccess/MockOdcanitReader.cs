using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Odmon.Worker.Models;

namespace Odmon.Worker.OdcanitAccess
{
    public class MockOdcanitReader : IOdcanitReader
    {
        private readonly List<OdcanitCase> _cases;

        public MockOdcanitReader()
        {
            _cases = new()
            {
                new OdcanitCase
                {
                    TikCounter = 1,
                    TikNumber = "1/1000",
                    TikName = "Test Case A",
                    ClientName = "ACME Inc.",
                    StatusName = "פתוח",
                    TikOwner = 10,
                    tsCreateDate = DateTime.UtcNow.AddDays(-2),
                    tsModifyDate = DateTime.UtcNow.AddMinutes(-13),
                    Notes = "Sample notes"
                },
                new OdcanitCase
                {
                    TikCounter = 2,
                    TikNumber = "2/2000",
                    TikName = "Test Case B",
                    ClientName = "Beta LLC",
                    StatusName = "סגור",
                    TikOwner = 11,
                    tsCreateDate = DateTime.UtcNow.AddDays(-10),
                    tsModifyDate = DateTime.UtcNow.AddMinutes(-60),
                    Notes = null
                }
            };
        }

        public Task<List<OdcanitCase>> GetCasesCreatedOnDateAsync(DateTime date, CancellationToken ct)
        {
            var start = date.Date;
            var end = start.AddDays(1);
            var result = _cases
                .Where(c => c.tsCreateDate >= start && c.tsCreateDate < end)
                .ToList();
            return Task.FromResult(result);
        }
    }
}

