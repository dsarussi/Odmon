using System.Threading;
using System.Threading.Tasks;

namespace Odmon.Worker.Services
{
    /// <summary>
    /// Persists structured skip events into IntegrationDb (dbo.SkipEvents).
    /// Failures must not fail the main run.
    /// </summary>
    public interface ISkipLogger
    {
        Task LogSkipAsync(
            int tikCounter,
            string? tikNumber,
            string operation,
            string reasonCode,
            string? entityId,
            string? rawValue,
            object? details,
            CancellationToken ct);
    }
}

