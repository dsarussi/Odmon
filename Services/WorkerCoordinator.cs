using System;
using System.Threading;
using System.Threading.Tasks;

namespace Odmon.Worker.Services
{
    /// <summary>
    /// In-process coordination gate that prevents SyncWorker,
    /// DocumentIngestionWorker and EmailBackgroundService from
    /// running their DB-heavy work concurrently.  Uses a
    /// <see cref="SemaphoreSlim(1,1)"/> — zero DB overhead,
    /// which is essential because lock-contention on the
    /// integration DB is the root cause of the timeout crashes.
    ///
    /// Registered as a singleton.
    /// </summary>
    public sealed class WorkerCoordinator
    {
        private readonly SemaphoreSlim _gate = new(1, 1);
        private volatile string? _activeWorker;

        /// <summary>Name of the worker that currently holds the lease, or null.</summary>
        public string? ActiveWorker => _activeWorker;

        /// <summary>
        /// Attempts to acquire the exclusive worker lease.
        /// Returns a disposable lease on success, or null if another worker is active.
        /// </summary>
        public async Task<WorkerLease?> TryAcquireAsync(
            string workerName, CancellationToken ct)
        {
            if (await _gate.WaitAsync(TimeSpan.Zero, ct))
            {
                _activeWorker = workerName;
                return new WorkerLease(this, workerName);
            }
            return null;
        }

        private void Release(string workerName)
        {
            if (_activeWorker == workerName)
                _activeWorker = null;
            _gate.Release();
        }

        public sealed class WorkerLease : IDisposable
        {
            private readonly WorkerCoordinator _coordinator;
            private readonly string _workerName;
            private int _disposed;

            internal WorkerLease(WorkerCoordinator coordinator, string workerName)
            {
                _coordinator = coordinator;
                _workerName = workerName;
            }

            public void Dispose()
            {
                if (Interlocked.Exchange(ref _disposed, 1) == 0)
                    _coordinator.Release(_workerName);
            }
        }
    }
}
