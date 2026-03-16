using Microsoft.Data.SqlClient;

namespace Odmon.Worker.Services
{
    /// <summary>
    /// Deterministic check for SQL connection-related failures (for critical alerts).
    /// Uses explicit error numbers only; no free-text matching.
    /// </summary>
    public static class SqlConnectionFailureDetector
    {
        /// <summary>SQL Server error numbers that indicate connection/timeout/network failure.</summary>
        private static readonly HashSet<int> ConnectionErrorNumbers = new()
        {
            -1,    // Timeout
            -2,    // Timeout
            2,     // Network-related
            53,    // Network path not found
            64,    // Network error
            233,   // Connection broken
            10053, // Connection abort
            10054, // Connection reset
            10060, // Network timeout
            20     // Fatal connection
        };

        public static bool IsConnectionFailure(SqlException ex)
        {
            if (ex == null) return false;
            return ConnectionErrorNumbers.Contains(ex.Number);
        }
    }
}
