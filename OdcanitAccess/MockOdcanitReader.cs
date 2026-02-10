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

        public Task<List<OdcanitCase>> GetCasesByTikCountersAsync(IEnumerable<int> tikCounters, CancellationToken ct)
        {
            var tikCounterSet = tikCounters?.ToHashSet() ?? new HashSet<int>();
            var result = _cases
                .Where(c => tikCounterSet.Contains(c.TikCounter))
                .ToList();
            return Task.FromResult(result);
        }

        public Task<List<OdcanitDiaryEvent>> GetDiaryEventsByTikCountersAsync(IEnumerable<int> tikCounters, CancellationToken ct)
        {
            return Task.FromResult(new List<OdcanitDiaryEvent>());
        }

        public Task<Dictionary<string, int>> ResolveTikNumbersToCountersAsync(IEnumerable<string> tikNumbers, CancellationToken ct)
        {
            // Mock implementation: resolve TikNumbers that exist in mock data
            var resolved = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var tikNumber in tikNumbers ?? Enumerable.Empty<string>())
            {
                var matchingCase = _cases.FirstOrDefault(c => c.TikNumber == tikNumber);
                if (matchingCase != null)
                {
                    resolved[tikNumber] = matchingCase.TikCounter;
                }
            }
            return Task.FromResult(resolved);
        }

        public Task<List<int>> GetTikCountersSinceCutoffAsync(DateTime cutoffDate, CancellationToken ct)
        {
            var cutoff = cutoffDate.Date;
            var result = _cases
                .Where(c => c.tsCreateDate.HasValue && c.tsCreateDate.Value.Date >= cutoff)
                .Select(c => c.TikCounter)
                .Distinct()
                .ToList();
            return Task.FromResult(result);
        }

        public Task<List<int>> GetModifiedTikCountersSinceAsync(DateTime sinceUtc, IReadOnlyCollection<int> eligibleMappedTikCounters, CancellationToken ct)
        {
            var eligible = eligibleMappedTikCounters?.ToHashSet() ?? new HashSet<int>();
            var result = _cases
                .Where(c => eligible.Contains(c.TikCounter)
                             && c.tsModifyDate.HasValue
                             && c.tsModifyDate.Value >= sinceUtc)
                .Select(c => c.TikCounter)
                .Distinct()
                .ToList();
            return Task.FromResult(result);
        }
    }
}

