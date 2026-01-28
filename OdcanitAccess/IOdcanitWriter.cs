using System;
using System.Threading;
using System.Threading.Tasks;
using Odmon.Worker.Models;

namespace Odmon.Worker.OdcanitAccess
{
    /// <summary>
    /// Abstraction for Phase-2 write-back into Odcanit (append Nispah records).
    /// </summary>
    public interface IOdcanitWriter
    {
        Task AppendNispahAsync(OdcanitCase c, DateTime nowUtc, string nispahType, string info, CancellationToken ct);
    }
}

