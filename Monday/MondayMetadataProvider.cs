using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Odmon.Worker.Security;

namespace Odmon.Worker.Monday
{
    public class MondayMetadataProvider : IMondayMetadataProvider
    {
        private const int BoardMetadataCacheTtlMinutes = 10;
        private readonly HttpClient _httpClient;
        private readonly ILogger<MondayMetadataProvider> _logger;
        private readonly Dictionary<long, BoardMetadataCacheEntry> _boardMetadataCache = new();

        public MondayMetadataProvider(
            HttpClient httpClient,
            IConfiguration config,
            ISecretProvider secretProvider,
            ILogger<MondayMetadataProvider> logger)
        {
            _httpClient = httpClient;
            _logger = logger;

            _logger.LogDebug(
                "Initializing MondayMetadataProvider. HttpClient BaseAddress: {BaseAddress}",
                _httpClient.BaseAddress?.ToString() ?? "<null>");

            var apiToken = secretProvider.GetSecret("Monday__ApiToken");
            if (string.IsNullOrWhiteSpace(apiToken) || IsPlaceholderValue(apiToken))
            {
                var fallback = config["Monday:ApiToken"];
                if (!string.IsNullOrWhiteSpace(fallback) && !IsPlaceholderValue(fallback))
                {
                    _logger.LogWarning("Monday API token retrieved from configuration fallback. Please migrate it to the configured secret store.");
                    apiToken = fallback;
                }
            }

            if (!string.IsNullOrWhiteSpace(apiToken) && !IsPlaceholderValue(apiToken))
            {
                _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiToken);
                _logger.LogInformation(
                    "Monday API token configured successfully (length: {TokenLength} chars)",
                    apiToken.Length);
            }
            else
            {
                _logger.LogError(
                    "Monday API token not found or is placeholder. Monday requests will fail. SecretProvider returned: {SecretProviderValue}, Config fallback: {ConfigFallback}",
                    string.IsNullOrWhiteSpace(apiToken) ? "<null/empty>" : "<placeholder>",
                    string.IsNullOrWhiteSpace(config["Monday:ApiToken"]) ? "<null/empty>" : config["Monday:ApiToken"]);
            }
        }

        private static bool IsPlaceholderValue(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return true;
            }

            if (value.Contains("CHANGE_ME", StringComparison.OrdinalIgnoreCase) ||
                value.Contains("__USE_SECRET__", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (value.Contains("__", StringComparison.Ordinal))
            {
                return true;
            }

            return false;
        }

        // ================================================================
        // Board-level metadata cache (single API call per board per TTL)
        // ================================================================

        public async Task<Dictionary<string, BoardColumnMetadata>> GetBoardColumnsMetadataAsync(long boardId, CancellationToken ct = default)
        {
            // Check cache with TTL
            lock (_boardMetadataCache)
            {
                if (_boardMetadataCache.TryGetValue(boardId, out var cached))
                {
                    var age = DateTime.UtcNow - cached.Timestamp;
                    if (age.TotalMinutes < BoardMetadataCacheTtlMinutes)
                    {
                        _logger.LogDebug("Using cached board columns metadata for board {BoardId} (age: {Age})", boardId, age);
                        return cached.Columns;
                    }
                    _logger.LogDebug("Board metadata cache expired for board {BoardId} (age: {Age}), refreshing", boardId, age);
                    _boardMetadataCache.Remove(boardId);
                }
            }

            // Single GraphQL call fetching all column metadata
            var query = @"query ($boardIds: [ID!]) {
                boards (ids: $boardIds) {
                    id
                    columns {
                        id
                        title
                        type
                        settings_str
                    }
                }
            }";

            var variables = new Dictionary<string, object>
            {
                ["boardIds"] = new[] { boardId.ToString() }
            };

            var payload = JsonSerializer.Serialize(new { query, variables });
            var content = new StringContent(payload, Encoding.UTF8, "application/json");

            _logger.LogDebug(
                "Calling Monday API for board columns metadata: board {BoardId}. BaseAddress: {BaseAddress}, HasAuthHeader: {HasAuthHeader}",
                boardId,
                _httpClient.BaseAddress,
                _httpClient.DefaultRequestHeaders.Authorization != null);

            var resp = await _httpClient.PostAsync("", content, ct);

            if (!resp.IsSuccessStatusCode)
            {
                var errorBody = await resp.Content.ReadAsStringAsync(ct);
                _logger.LogError(
                    "Monday API request FAILED with status {StatusCode} for board columns metadata on board {BoardId}. Response: {ResponseBody}",
                    resp.StatusCode,
                    boardId,
                    errorBody.Length > 500 ? errorBody.Substring(0, 500) + "..." : errorBody);
            }

            resp.EnsureSuccessStatusCode();
            var body = await resp.Content.ReadAsStringAsync(ct);

            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;

            if (root.TryGetProperty("errors", out var errors))
            {
                throw new InvalidOperationException($"Monday.com API error while fetching board columns metadata: {errors}");
            }

            if (!root.TryGetProperty("data", out var data) ||
                !data.TryGetProperty("boards", out var boards) ||
                boards.ValueKind != JsonValueKind.Array ||
                boards.GetArrayLength() == 0)
            {
                throw new InvalidOperationException($"Monday.com unexpected response when fetching board columns metadata for board {boardId}: {body}");
            }

            var board = boards[0];
            if (!board.TryGetProperty("columns", out var columns) ||
                columns.ValueKind != JsonValueKind.Array)
            {
                throw new InvalidOperationException($"Monday.com board {boardId} has no columns in metadata response: {body}");
            }

            var result = new Dictionary<string, BoardColumnMetadata>(StringComparer.Ordinal);
            foreach (var column in columns.EnumerateArray())
            {
                var colId = column.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
                if (string.IsNullOrWhiteSpace(colId)) continue;

                result[colId] = new BoardColumnMetadata
                {
                    ColumnId = colId,
                    Title = column.TryGetProperty("title", out var titleEl) ? titleEl.GetString() : null,
                    ColumnType = column.TryGetProperty("type", out var typeEl) ? typeEl.GetString() : null,
                    SettingsStr = column.TryGetProperty("settings_str", out var settingsEl) ? settingsEl.GetString() : null
                };
            }

            // Cache on success only
            lock (_boardMetadataCache)
            {
                _boardMetadataCache[boardId] = new BoardMetadataCacheEntry
                {
                    Columns = result,
                    Timestamp = DateTime.UtcNow
                };
            }

            _logger.LogInformation(
                "Fetched and cached {Count} column(s) metadata for board {BoardId}",
                result.Count, boardId);

            return result;
        }

        // ================================================================
        // GetColumnIdByTitleAsync – delegates to board cache
        // ================================================================

        public async Task<string> GetColumnIdByTitleAsync(long boardId, string title, CancellationToken ct = default)
        {
            var columns = await GetBoardColumnsMetadataAsync(boardId, ct);

            foreach (var kvp in columns)
            {
                if (string.Equals(kvp.Value.Title, title, StringComparison.Ordinal))
                {
                    return kvp.Key;
                }
            }

            throw new InvalidOperationException(
                $"Column with title '{title}' not found on Monday board {boardId}. " +
                $"Available columns: {string.Join(", ", columns.Values.Select(c => c.Title))}");
        }

        // ================================================================
        // GetAllowedDropdownLabelsAsync – parses labels from board cache
        // ================================================================

        public async Task<HashSet<string>> GetAllowedDropdownLabelsAsync(long boardId, string columnId, CancellationToken ct = default)
        {
            try
            {
                var columns = await GetBoardColumnsMetadataAsync(boardId, ct);

                if (!columns.TryGetValue(columnId, out var meta))
                {
                    var availableColumns = string.Join(", ", columns.Keys);
                    _logger.LogError(
                        "Column {ColumnId} not found on board {BoardId} when resolving allowed labels. Available columns: {AvailableColumns}",
                        columnId, boardId, availableColumns);

                    throw new InvalidOperationException(
                        $"Column {columnId} not found on board {boardId}. " +
                        $"This indicates a configuration mismatch. Available columns: {availableColumns}");
                }

                if (string.IsNullOrWhiteSpace(meta.SettingsStr))
                {
                    _logger.LogError(
                        "Column {ColumnId} (type={ColumnType}) on board {BoardId} has no/empty settings_str; cannot resolve allowed labels.",
                        columnId, meta.ColumnType, boardId);

                    throw new InvalidOperationException(
                        $"Column {columnId} (type={meta.ColumnType}) on board {boardId} has empty settings_str. " +
                        $"Cannot resolve allowed labels.");
                }

                _logger.LogDebug(
                    "Parsing settings_str for column {ColumnId} on board {BoardId}: {SettingsStrPreview}",
                    columnId, boardId,
                    meta.SettingsStr.Length > 200 ? meta.SettingsStr.Substring(0, 200) + "..." : meta.SettingsStr);

                using var settingsDoc = JsonDocument.Parse(meta.SettingsStr);
                var settingsRoot = settingsDoc.RootElement;

                var labels = new HashSet<string>(StringComparer.Ordinal);

                // Dropdown labels: array of { "name": "..." }
                if (settingsRoot.TryGetProperty("labels", out var labelsElement) &&
                    labelsElement.ValueKind == JsonValueKind.Array)
                {
                    foreach (var label in labelsElement.EnumerateArray())
                    {
                        if (label.TryGetProperty("name", out var nameElement))
                        {
                            var name = nameElement.GetString();
                            if (!string.IsNullOrWhiteSpace(name))
                            {
                                labels.Add(name);
                                _logger.LogDebug(
                                    "Found label for column {ColumnId}: '{LabelName}'",
                                    columnId, name);
                            }
                        }
                    }
                }
                else
                {
                    var columnType = meta.ColumnType ?? "unknown";
                    _logger.LogWarning(
                        "Column {ColumnId} (type={ColumnType}) on board {BoardId} has settings_str but no 'labels' array. SettingsStr keys: {SettingsKeys}",
                        columnId, columnType, boardId,
                        string.Join(", ", settingsRoot.EnumerateObject().Select(p => p.Name)));
                }

                if (labels.Count > 0)
                {
                    _logger.LogInformation(
                        "Resolved {Count} allowed label(s) for column {ColumnId} on board {BoardId}: [{Labels}]",
                        labels.Count, columnId, boardId,
                        string.Join(", ", labels));
                }
                else
                {
                    _logger.LogError(
                        "No labels found for DROPDOWN column {ColumnId} on board {BoardId}. Settings parsed but labels array was empty or invalid. This should not happen for a valid dropdown column.",
                        columnId, boardId);

                    throw new InvalidOperationException(
                        $"No labels found for dropdown column {columnId} on board {boardId}. " +
                        $"Settings parsed but labels array was empty or invalid.");
                }

                return labels;
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "Failed to fetch/parse allowed labels for column {ColumnId} on board {BoardId}. Exception: {ExceptionType}, Message: {ExceptionMessage}",
                    columnId, boardId,
                    ex.GetType().Name, ex.Message);

                // DO NOT return empty labels - this hides infrastructure failures
                // Throw to surface the real issue (auth, network, config, etc.)
                throw new InvalidOperationException(
                    $"Failed to fetch allowed labels for column {columnId} on board {boardId}. " +
                    $"This is likely an infrastructure issue (auth/network/config). Exception: {ex.Message}",
                    ex);
            }
        }

        // ================================================================
        // GetAllowedStatusLabelsAsync – parses status labels from board cache
        // ================================================================

        public async Task<HashSet<string>> GetAllowedStatusLabelsAsync(long boardId, string columnId, CancellationToken ct = default)
        {
            try
            {
                var columns = await GetBoardColumnsMetadataAsync(boardId, ct);

                if (!columns.TryGetValue(columnId, out var meta))
                {
                    var availableColumns = string.Join(", ", columns.Keys);
                    _logger.LogError(
                        "Status column {ColumnId} not found on board {BoardId} when resolving allowed labels. Available columns: {AvailableColumns}",
                        columnId, boardId, availableColumns);

                    throw new InvalidOperationException(
                        $"Status column {columnId} not found on board {boardId}. " +
                        $"This indicates a configuration mismatch. Available columns: {availableColumns}");
                }

                if (string.IsNullOrWhiteSpace(meta.SettingsStr))
                {
                    _logger.LogError(
                        "Status column {ColumnId} (type={ColumnType}) on board {BoardId} has no/empty settings_str; cannot resolve allowed labels.",
                        columnId, meta.ColumnType, boardId);

                    throw new InvalidOperationException(
                        $"Status column {columnId} (type={meta.ColumnType}) on board {boardId} has empty settings_str. " +
                        $"Cannot resolve allowed labels.");
                }

                _logger.LogDebug(
                    "Parsing settings_str for STATUS column {ColumnId} on board {BoardId}: {SettingsStrPreview}",
                    columnId, boardId,
                    meta.SettingsStr.Length > 200 ? meta.SettingsStr.Substring(0, 200) + "..." : meta.SettingsStr);

                using var settingsDoc = JsonDocument.Parse(meta.SettingsStr);
                var settingsRoot = settingsDoc.RootElement;

                var labels = new HashSet<string>(StringComparer.Ordinal);

                // STATUS columns: labels is a DICTIONARY where values are the label strings
                // Example: {"labels": {"0": "כתב תביעה", "1": "כתב הגנה", "11": "תצהיר עד ראשי"}}
                if (settingsRoot.TryGetProperty("labels", out var labelsElement) &&
                    labelsElement.ValueKind == JsonValueKind.Object)
                {
                    foreach (var labelProperty in labelsElement.EnumerateObject())
                    {
                        var labelValue = labelProperty.Value.GetString();
                        if (!string.IsNullOrWhiteSpace(labelValue))
                        {
                            labels.Add(labelValue);
                            _logger.LogDebug(
                                "Found STATUS label for column {ColumnId} (key={Key}): '{LabelValue}'",
                                columnId, labelProperty.Name, labelValue);
                        }
                    }
                }
                else
                {
                    _logger.LogWarning(
                        "Status column {ColumnId} on board {BoardId} has settings_str but 'labels' is not an object. ValueKind: {ValueKind}, SettingsStr keys: {SettingsKeys}",
                        columnId, boardId,
                        labelsElement.ValueKind,
                        string.Join(", ", settingsRoot.EnumerateObject().Select(p => p.Name)));
                }

                if (labels.Count > 0)
                {
                    _logger.LogInformation(
                        "Resolved {Count} allowed STATUS label(s) for column {ColumnId} on board {BoardId}: [{Labels}]",
                        labels.Count, columnId, boardId,
                        string.Join(", ", labels));
                }
                else
                {
                    _logger.LogError(
                        "No labels found for STATUS column {ColumnId} on board {BoardId}. Settings parsed but labels object was empty or invalid. This should not happen for a valid status column.",
                        columnId, boardId);

                    throw new InvalidOperationException(
                        $"No labels found for status column {columnId} on board {boardId}. " +
                        $"Settings parsed but labels object was empty or invalid.");
                }

                return labels;
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "Failed to fetch/parse allowed STATUS labels for column {ColumnId} on board {BoardId}. Exception: {ExceptionType}, Message: {ExceptionMessage}",
                    columnId, boardId,
                    ex.GetType().Name, ex.Message);

                // DO NOT return empty labels - this hides infrastructure failures
                // Throw to surface the real issue (auth, network, config, etc.)
                throw new InvalidOperationException(
                    $"Failed to fetch allowed STATUS labels for column {columnId} on board {boardId}. " +
                    $"This is likely an infrastructure issue (auth/network/config). Exception: {ex.Message}",
                    ex);
            }
        }

        // ================================================================
        // GetColumnTypeAsync – looks up type from board cache
        // ================================================================

        public async Task<string?> GetColumnTypeAsync(long boardId, string columnId, CancellationToken ct = default)
        {
            try
            {
                var columns = await GetBoardColumnsMetadataAsync(boardId, ct);

                if (columns.TryGetValue(columnId, out var meta) && !string.IsNullOrWhiteSpace(meta.ColumnType))
                {
                    _logger.LogDebug(
                        "Resolved column type for {ColumnId} on board {BoardId}: {ColumnType}",
                        columnId, boardId, meta.ColumnType);
                    return meta.ColumnType;
                }

                _logger.LogWarning(
                    "Column {ColumnId} not found on board {BoardId} when resolving type",
                    columnId, boardId);
                return null;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "Failed to fetch column type for column {ColumnId} on board {BoardId}",
                    columnId, boardId);
                return null;
            }
        }

        // ================================================================
        // Internal types exposed for testing
        // ================================================================

        internal class BoardMetadataCacheEntry
        {
            public Dictionary<string, BoardColumnMetadata> Columns { get; set; } = new();
            public DateTime Timestamp { get; set; }
        }

        /// <summary>Exposed for testing: allows inspecting/manipulating board metadata cache entries.</summary>
        internal Dictionary<long, BoardMetadataCacheEntry> BoardMetadataCache => _boardMetadataCache;
    }
}
