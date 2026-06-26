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
            "update_skipped_inactive",
            "hearing_update_skipped_inactive",
            "hearing_update_skipped_not_ready_or_inactive",
            "update_skipped_not_ready",
            "netcourt_skipped_missing_routing",
            "duplicate_idempotent_skip"
        };

        private static readonly HashSet<string> KnownDataIssueErrorTypes = new(StringComparer.Ordinal)
        {
            "KnownBlockedClient",
            "KnownDataIssue",
            "InvalidMapping"
        };

        private static readonly HashSet<string> SkippedExpectedErrorTypes = new(StringComparer.Ordinal)
        {
            "SkippedMissingRouting",
            "AttachmentSkippedTooLarge",
            "DuplicateIdempotentSkip"
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
                return FailureCategory.SkippedExpected;

            if (errorType != null && SkippedExpectedErrorTypes.Contains(errorType))
                return FailureCategory.SkippedExpected;

            if (errorType != null && KnownDataIssueErrorTypes.Contains(errorType))
                return FailureCategory.KnownDataIssue;

            if (errorType != null && CriticalErrorTypes.Contains(errorType))
                return FailureCategory.Critical;

            return FailureCategory.Operational;
        }

        /// <summary>
        /// Returns true if this failure should be included in "Failures That Require Attention" (excludes Ignored).
        /// </summary>
        public static bool IsRealFailure(string? errorType, string? operation)
        {
            var category = Classify(errorType, operation);
            return category is not FailureCategory.Ignored
                and not FailureCategory.KnownDataIssue
                and not FailureCategory.SkippedExpected;
        }
    }
}
