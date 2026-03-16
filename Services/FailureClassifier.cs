using System.Collections.Generic;
using Odmon.Worker.Models;

namespace Odmon.Worker.Services
{
    /// <summary>
    /// Deterministic classification of sync failures by operation and exception type.
    /// Uses explicit operation names and exception type names only (no free-text matching).
    /// </summary>
    public static class FailureClassifier
    {
        /// <summary>Operations that are expected skips; do not show in daily summary or alert.</summary>
        private static readonly HashSet<string> IgnoredOperations = new(StringComparer.Ordinal)
        {
            "update_skipped_inactive"
        };

        /// <summary>Exception types that indicate infrastructure/system failure; trigger immediate alerts.</summary>
        private static readonly HashSet<string> CriticalErrorTypes = new(StringComparer.Ordinal)
        {
            "HttpRequestException",
            "TaskCanceledException",
            "TimeoutException",
            "SqlException",
            "OperationCanceledException",
            "SocketException",
            "IOException"
        };

        /// <summary>
        /// Classifies a persisted failure for daily summary and alert behavior.
        /// Deterministic: only Operation and ErrorType are used.
        /// </summary>
        public static FailureCategory Classify(string? errorType, string? operation)
        {
            if (operation != null && IgnoredOperations.Contains(operation))
                return FailureCategory.Ignored;

            if (errorType != null && CriticalErrorTypes.Contains(errorType))
                return FailureCategory.Critical;

            return FailureCategory.Operational;
        }

        /// <summary>
        /// Returns true if this failure should be included in "Failures That Require Attention" (excludes Ignored).
        /// </summary>
        public static bool IsRealFailure(string? errorType, string? operation)
        {
            return Classify(errorType, operation) != FailureCategory.Ignored;
        }
    }
}
