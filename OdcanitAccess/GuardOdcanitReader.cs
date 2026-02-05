using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Odmon.Worker.Models;

namespace Odmon.Worker.OdcanitAccess
{
    /// <summary>
    /// Guard implementation that hard-blocks Odcanit access in Testing mode.
    /// Any call to this reader indicates a bug in the test-mode wiring.
    /// </summary>
    public class GuardOdcanitReader : IOdcanitReader
    {
        private static InvalidOperationException NewException()
            => new InvalidOperationException("Odcanit access is blocked when Testing.Enable=true.");

        public Task<List<OdcanitCase>> GetCasesCreatedOnDateAsync(DateTime date, CancellationToken ct)
            => throw NewException();

        public Task<List<OdcanitCase>> GetCasesByTikCountersAsync(IEnumerable<int> tikCounters, CancellationToken ct)
            => throw NewException();

        public Task<List<OdcanitDiaryEvent>> GetDiaryEventsByTikCountersAsync(IEnumerable<int> tikCounters, CancellationToken ct)
            => throw NewException();

        public Task<Dictionary<string, int>> ResolveTikNumbersToCountersAsync(IEnumerable<string> tikNumbers, CancellationToken ct)
            => throw NewException();
    }
}

