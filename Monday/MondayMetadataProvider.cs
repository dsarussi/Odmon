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
        private const int DropdownLabelsCacheTtlMinutes = 15;
        private readonly HttpClient _httpClient;
        private readonly ILogger<MondayMetadataProvider> _logger;
        private readonly Dictionary<(long boardId, string title), string> _cache = new();
        private readonly Dictionary<(long boardId, string columnId), DropdownLabelsCacheEntry> _dropdownLabelsCache = new();
        private readonly Dictionary<(long boardId, string columnId), string> _columnTypeCache = new();

        public MondayMetadataProvider(
            HttpClient httpClient,
            IConfiguration config,
            ISecretProvider secretProvider,
            ILogger<MondayMetadataProvider> logger)
        {
            _httpClient = httpClient;
            _logger = logger;

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
            }
            else
            {
                _logger.LogError("Monday API token not found. Monday requests will fail until the secret 'Monday__ApiToken' is configured.");
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

        public async Task<string> GetColumnIdByTitleAsync(long boardId, string title, CancellationToken ct = default)
        {
            var cacheKey = (boardId, title);
            lock (_cache)
            {
                if (_cache.TryGetValue(cacheKey, out var cachedId))
                {
                    return cachedId;
                }
            }

            var query = @"query ($boardIds: [ID!]) {
                boards (ids: $boardIds) {
                    id
                    columns {
                        id
                        title
                    }
                }
            }";

            var variables = new Dictionary<string, object>
            {
                ["boardIds"] = new[] { boardId.ToString() }
            };

            var payload = JsonSerializer.Serialize(new { query, variables });
            var content = new StringContent(payload, Encoding.UTF8, "application/json");

            var resp = await _httpClient.PostAsync("", content, ct);
            resp.EnsureSuccessStatusCode();
            var body = await resp.Content.ReadAsStringAsync(ct);

            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;

            if (root.TryGetProperty("errors", out var errors))
            {
                throw new InvalidOperationException($"Monday.com API error while fetching column metadata: {errors}");
            }

            if (!root.TryGetProperty("data", out var data) ||
                !data.TryGetProperty("boards", out var boards) ||
                boards.ValueKind != System.Text.Json.JsonValueKind.Array ||
                boards.GetArrayLength() == 0)
            {
                throw new InvalidOperationException($"Monday.com unexpected response when fetching board metadata: {body}");
            }

            var board = boards[0];
            if (!board.TryGetProperty("columns", out var columns) ||
                columns.ValueKind != System.Text.Json.JsonValueKind.Array)
            {
                throw new InvalidOperationException($"Monday.com board {boardId} has no columns in response: {body}");
            }

            foreach (var column in columns.EnumerateArray())
            {
                if (column.TryGetProperty("title", out var columnTitle) &&
                    column.TryGetProperty("id", out var columnId))
                {
                    var columnTitleValue = columnTitle.GetString();
                    var columnIdValue = columnId.GetString();

                    if (columnTitleValue == title && !string.IsNullOrWhiteSpace(columnIdValue))
                    {
                        lock (_cache)
                        {
                            _cache[cacheKey] = columnIdValue;
                        }
                        return columnIdValue;
                    }
                }
            }

            throw new InvalidOperationException($"Column with title '{title}' not found on Monday board {boardId}. Available columns: {string.Join(", ", columns.EnumerateArray().Select(c => c.TryGetProperty("title", out var t) ? t.GetString() : "?"))}");
        }

        public async Task<HashSet<string>> GetAllowedDropdownLabelsAsync(long boardId, string columnId, CancellationToken ct = default)
        {
            var cacheKey = (boardId, columnId);
            
            // Check cache with TTL
            lock (_dropdownLabelsCache)
            {
                if (_dropdownLabelsCache.TryGetValue(cacheKey, out var cachedEntry))
                {
                    var age = DateTime.UtcNow - cachedEntry.Timestamp;
                    if (age.TotalMinutes < DropdownLabelsCacheTtlMinutes)
                    {
                        _logger.LogDebug("Using cached dropdown labels for column {ColumnId} on board {BoardId} (age: {Age})", columnId, boardId, age);
                        return cachedEntry.Labels;
                    }
                    else
                    {
                        _logger.LogDebug("Dropdown labels cache expired for column {ColumnId} on board {BoardId} (age: {Age}), refreshing", columnId, boardId, age);
                        _dropdownLabelsCache.Remove(cacheKey);
                    }
                }
            }

            try
            {
                var query = @"query ($boardIds: [ID!]) {
                    boards (ids: $boardIds) {
                        id
                        columns {
                            id
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

                var resp = await _httpClient.PostAsync("", content, ct);
                resp.EnsureSuccessStatusCode();
                var body = await resp.Content.ReadAsStringAsync(ct);

                using var doc = JsonDocument.Parse(body);
                var root = doc.RootElement;

                if (root.TryGetProperty("errors", out var errors))
                {
                    _logger.LogWarning("Monday.com API error while fetching dropdown metadata for board {BoardId}: {Errors}", boardId, errors.ToString());
                    throw new InvalidOperationException($"Monday.com API error while fetching dropdown metadata: {errors}");
                }

                if (!root.TryGetProperty("data", out var data) ||
                    !data.TryGetProperty("boards", out var boards) ||
                    boards.ValueKind != JsonValueKind.Array ||
                    boards.GetArrayLength() == 0)
                {
                    throw new InvalidOperationException($"Monday.com unexpected response when fetching dropdown metadata for board {boardId}: {body}");
                }

                var board = boards[0];
                if (!board.TryGetProperty("columns", out var columns) ||
                    columns.ValueKind != JsonValueKind.Array)
                {
                    throw new InvalidOperationException($"Monday.com board {boardId} has no columns in dropdown metadata response: {body}");
                }

                JsonElement? targetColumn = null;
                foreach (var column in columns.EnumerateArray())
                {
                    if (column.TryGetProperty("id", out var idElement))
                    {
                        var idValue = idElement.GetString();
                        if (string.Equals(idValue, columnId, StringComparison.Ordinal))
                        {
                            targetColumn = column;
                            break;
                        }
                    }
                }

                if (targetColumn is null)
                {
                    _logger.LogWarning(
                        "Dropdown column {ColumnId} not found on board {BoardId} when resolving allowed labels.",
                        columnId,
                        boardId);
                    var empty = new HashSet<string>(StringComparer.Ordinal);
                    lock (_dropdownLabelsCache)
                    {
                        _dropdownLabelsCache[cacheKey] = new DropdownLabelsCacheEntry
                        {
                            Labels = empty,
                            Timestamp = DateTime.UtcNow
                        };
                    }
                    return empty;
                }

                if (!targetColumn.Value.TryGetProperty("settings_str", out var settingsElement))
                {
                    _logger.LogWarning(
                        "Dropdown column {ColumnId} on board {BoardId} has no settings_str; cannot resolve allowed labels.",
                        columnId,
                        boardId);
                    var empty = new HashSet<string>(StringComparer.Ordinal);
                    lock (_dropdownLabelsCache)
                    {
                        _dropdownLabelsCache[cacheKey] = new DropdownLabelsCacheEntry
                        {
                            Labels = empty,
                            Timestamp = DateTime.UtcNow
                        };
                    }
                    return empty;
                }

                var settingsStr = settingsElement.GetString();
                if (string.IsNullOrWhiteSpace(settingsStr))
                {
                    var empty = new HashSet<string>(StringComparer.Ordinal);
                    lock (_dropdownLabelsCache)
                    {
                        _dropdownLabelsCache[cacheKey] = new DropdownLabelsCacheEntry
                        {
                            Labels = empty,
                            Timestamp = DateTime.UtcNow
                        };
                    }
                    return empty;
                }

                using var settingsDoc = JsonDocument.Parse(settingsStr);
                var settingsRoot = settingsDoc.RootElement;

                var labels = new HashSet<string>(StringComparer.Ordinal);
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
                            }
                        }
                    }
                }

                lock (_dropdownLabelsCache)
                {
                    _dropdownLabelsCache[cacheKey] = new DropdownLabelsCacheEntry
                    {
                        Labels = labels,
                        Timestamp = DateTime.UtcNow
                    };
                }

                _logger.LogDebug(
                    "Resolved {Count} allowed dropdown label(s) for column {ColumnId} on board {BoardId}.",
                    labels.Count,
                    columnId,
                    boardId);

                return labels;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "Failed to fetch/parse allowed dropdown labels for column {ColumnId} on board {BoardId}.",
                    columnId,
                    boardId);

                var empty = new HashSet<string>(StringComparer.Ordinal);
                lock (_dropdownLabelsCache)
                {
                    _dropdownLabelsCache[cacheKey] = new DropdownLabelsCacheEntry
                    {
                        Labels = empty,
                        Timestamp = DateTime.UtcNow
                    };
                }
                return empty;
            }
        }

        public async Task<HashSet<string>> GetAllowedStatusLabelsAsync(long boardId, string columnId, CancellationToken ct = default)
        {
            // Status columns use the same structure as dropdown columns in Monday API
            // Both have settings_str with labels array
            return await GetAllowedDropdownLabelsAsync(boardId, columnId, ct);
        }

        public async Task<string?> GetColumnTypeAsync(long boardId, string columnId, CancellationToken ct = default)
        {
            var cacheKey = (boardId, columnId);
            
            // Check cache
            lock (_columnTypeCache)
            {
                if (_columnTypeCache.TryGetValue(cacheKey, out var cachedType))
                {
                    return cachedType;
                }
            }

            try
            {
                var query = @"query ($boardIds: [ID!]) {
                    boards (ids: $boardIds) {
                        id
                        columns {
                            id
                            type
                        }
                    }
                }";

                var variables = new Dictionary<string, object>
                {
                    ["boardIds"] = new[] { boardId.ToString() }
                };

                var payload = JsonSerializer.Serialize(new { query, variables });
                var content = new StringContent(payload, Encoding.UTF8, "application/json");

                var resp = await _httpClient.PostAsync("", content, ct);
                resp.EnsureSuccessStatusCode();
                var body = await resp.Content.ReadAsStringAsync(ct);

                using var doc = JsonDocument.Parse(body);
                var root = doc.RootElement;

                if (root.TryGetProperty("errors", out var errors))
                {
                    _logger.LogWarning("Monday.com API error while fetching column type for board {BoardId}: {Errors}", boardId, errors.ToString());
                    return null;
                }

                if (!root.TryGetProperty("data", out var data) ||
                    !data.TryGetProperty("boards", out var boards) ||
                    boards.ValueKind != JsonValueKind.Array ||
                    boards.GetArrayLength() == 0)
                {
                    _logger.LogWarning("Monday.com unexpected response when fetching column type for board {BoardId}", boardId);
                    return null;
                }

                var board = boards[0];
                if (!board.TryGetProperty("columns", out var columns) ||
                    columns.ValueKind != JsonValueKind.Array)
                {
                    _logger.LogWarning("Monday.com board {BoardId} has no columns in type response", boardId);
                    return null;
                }

                foreach (var column in columns.EnumerateArray())
                {
                    if (column.TryGetProperty("id", out var idElement))
                    {
                        var idValue = idElement.GetString();
                        if (string.Equals(idValue, columnId, StringComparison.Ordinal))
                        {
                            if (column.TryGetProperty("type", out var typeElement))
                            {
                                var columnType = typeElement.GetString();
                                if (!string.IsNullOrWhiteSpace(columnType))
                                {
                                    lock (_columnTypeCache)
                                    {
                                        _columnTypeCache[cacheKey] = columnType;
                                    }
                                    _logger.LogDebug(
                                        "Resolved column type for {ColumnId} on board {BoardId}: {ColumnType}",
                                        columnId,
                                        boardId,
                                        columnType);
                                    return columnType;
                                }
                            }
                        }
                    }
                }

                _logger.LogWarning(
                    "Column {ColumnId} not found on board {BoardId} when resolving type",
                    columnId,
                    boardId);
                return null;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "Failed to fetch column type for column {ColumnId} on board {BoardId}",
                    columnId,
                    boardId);
                return null;
            }
        }

        private class DropdownLabelsCacheEntry
        {
            public HashSet<string> Labels { get; set; } = new();
            public DateTime Timestamp { get; set; }
        }
    }
}

