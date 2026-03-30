using Microsoft.Data.SqlClient;

namespace Odmon.Worker.Services
{
    /// <summary>
    /// Deterministic check for SQL failure classification (for critical alerts).
    /// Uses explicit error numbers only; no free-text matching.
    /// </summary>
    public static class SqlConnectionFailureDetector
    {
        private static readonly HashSet<int> TimeoutErrorNumbers = new()
        {
            -1,    // General timeout / transport-level
            -2,    // Command execution timeout (CommandTimeout exceeded)
        };

        private static readonly HashSet<int> NetworkErrorNumbers = new()
        {
            2,     // Network-related
            53,    // Network path not found
            64,    // Network error
            233,   // Connection broken
            10053, // Connection abort
            10054, // Connection reset
            10060, // Network timeout
            20     // Fatal connection
        };

        /// <summary>True for query/command timeouts (error -1, -2).</summary>
        public static bool IsQueryTimeout(SqlException ex)
        {
            if (ex == null) return false;
            return TimeoutErrorNumbers.Contains(ex.Number);
        }

        /// <summary>True for network/connection failures (does NOT include query timeouts).</summary>
        public static bool IsNetworkFailure(SqlException ex)
        {
            if (ex == null) return false;
            return NetworkErrorNumbers.Contains(ex.Number);
        }

        /// <summary>True for either timeout or network failure (backward-compatible).</summary>
        public static bool IsConnectionFailure(SqlException ex)
        {
            if (ex == null) return false;
            return IsQueryTimeout(ex) || IsNetworkFailure(ex);
        }
    }
}
