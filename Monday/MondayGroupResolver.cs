using System;
using System.Collections.Generic;
using Microsoft.Extensions.Logging;

namespace Odmon.Worker.Monday
{
    /// <summary>
    /// Resolves a Monday board group ID for create_item: uses the requested ID if it exists on the board,
    /// otherwise falls back to the first group. Safe for any board/environment; no hardcoded group IDs.
    /// Why this is safe: We only read the board's current groups from Monday and pick one that exists.
    /// We never change Monday data. Non-bootstrap flows are unchanged (they still pass groupId to create_item;
    /// only Bootstrap resolves before creating). Valid configured groupId is used unchanged.
    /// </summary>
    public static class MondayGroupResolver
    {
        /// <summary>
        /// Resolves the group ID to use for create_item. If requestedGroupId is null/empty or not in
        /// boardGroupIds (case-sensitive), returns the first group and logs a fallback. If the board
        /// has zero groups, throws.
        /// </summary>
        /// <param name="boardId">Board ID (for logging).</param>
        /// <param name="boardGroupIds">Group IDs returned by Monday for the board.</param>
        /// <param name="requestedGroupId">Configured group ID (e.g. from Monday:ToDoGroupId).</param>
        /// <param name="requestedGroupIdSource">Source of the requested value (e.g. "Monday:ToDoGroupId") for logging.</param>
        /// <param name="logger">Logger for fallback and error.</param>
        /// <returns>The resolved group ID (requested if valid, otherwise first group).</returns>
        public static string ResolveGroupId(
            long boardId,
            IReadOnlyList<string> boardGroupIds,
            string? requestedGroupId,
            string requestedGroupIdSource,
            ILogger logger)
        {
            if (boardGroupIds == null || boardGroupIds.Count == 0)
            {
                logger.LogError(
                    "MondayGroupResolver: Board {BoardId} has no groups. Cannot create items. Add at least one group on the board.",
                    boardId);
                throw new InvalidOperationException(
                    $"Board {boardId} has no groups. Cannot create items. Add at least one group on the Monday board.");
            }

            var useRequested = !string.IsNullOrWhiteSpace(requestedGroupId) &&
                              ContainsGroupId(boardGroupIds, requestedGroupId!);

            if (useRequested)
            {
                logger.LogDebug(
                    "MondayGroupResolver: Using configured group | BoardId={BoardId}, GroupId={GroupId}, Source={Source}",
                    boardId, requestedGroupId, requestedGroupIdSource);
                return requestedGroupId!;
            }

            var fallback = boardGroupIds[0];
            logger.LogWarning(
                "MondayGroupResolver: Requested group missing or invalid, using first group | BoardId={BoardId}, RequestedGroupId={RequestedGroupId}, ChosenFallbackGroupId={FallbackGroupId}, Source={Source}",
                boardId, requestedGroupId ?? "(null/empty)", fallback, requestedGroupIdSource);
            return fallback;
        }

        private static bool ContainsGroupId(IReadOnlyList<string> boardGroupIds, string requestedGroupId)
        {
            for (int i = 0; i < boardGroupIds.Count; i++)
            {
                if (string.Equals(boardGroupIds[i], requestedGroupId, StringComparison.Ordinal))
                    return true;
            }
            return false;
        }
    }
}
