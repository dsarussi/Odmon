using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using Microsoft.Extensions.Configuration;
using System.Text.Json;
using System.Net.Http.Headers;
using Microsoft.Extensions.Logging;
using System.Globalization;
using Odmon.Worker.Security;

namespace Odmon.Worker.Monday
{
    public class MondayClient : IMondayClient
    {
        private readonly HttpClient _httpClient;
        private readonly long _boardId;
        private readonly ILogger<MondayClient> _logger;

        public MondayClient(
            HttpClient httpClient,
            IConfiguration config,
            ISecretProvider secretProvider,
            ILogger<MondayClient> logger)
        {
            _httpClient = httpClient;
            _logger = logger;
            _boardId = config.GetValue<long>("Monday:BoardId");

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

            // Check for common placeholder patterns
            if (value.Contains("CHANGE_ME", StringComparison.OrdinalIgnoreCase) ||
                value.Contains("__USE_SECRET__", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            // Check if the value looks like a secret key name (contains "__")
            // Secret keys follow the pattern: Something__SomethingElse (e.g., "Monday__ApiToken")
            if (value.Contains("__", StringComparison.Ordinal))
            {
                // If it contains "__", it's likely a secret key reference
                return true;
            }

            return false;
        }

        public async Task<long> CreateItemAsync(long boardId, string groupId, string itemName, string columnValuesJson, CancellationToken ct)
        {
            var query = @"mutation ($boardId: ID!, $groupId: String!, $itemName: String!, $columnVals: JSON!) {
                create_item (board_id: $boardId, group_id: $groupId, item_name: $itemName, column_values: $columnVals) {
                    id
                }
            }";

            var variables = new Dictionary<string, object>
            {
                ["boardId"] = boardId.ToString(),
                ["groupId"] = groupId,
                ["itemName"] = itemName,
                ["columnVals"] = columnValuesJson,
            };

            try
            {
                using var doc = await ExecuteGraphQLRequestAsync(query, variables, ct, "create_item", boardId, null, columnValuesJson);
                var root = doc.RootElement;

                if (!root.TryGetProperty("data", out var data) ||
                    !data.TryGetProperty("create_item", out var createItem) ||
                    !createItem.TryGetProperty("id", out var idElement))
                {
                    throw new MondayApiException(
                        "Monday.com unexpected response: missing create_item.id in response",
                        operation: "create_item",
                        boardId: boardId,
                        columnValuesSnippet: columnValuesJson);
                }

                var idString = idElement.GetString();
                if (string.IsNullOrWhiteSpace(idString))
                {
                    throw new MondayApiException(
                        "Monday.com API returned empty id",
                        operation: "create_item",
                        boardId: boardId,
                        columnValuesSnippet: columnValuesJson);
                }

                return long.Parse(idString);
            }
            catch (MondayApiException)
            {
                throw;
            }
            catch (Exception ex) when (!(ex is MondayApiException))
            {
                throw new MondayApiException(
                    $"Unexpected error during create_item: {ex.Message}",
                    ex,
                    operation: "create_item",
                    boardId: boardId,
                    columnValuesSnippet: columnValuesJson);
            }
        }

        public async Task<string?> GetItemStateAsync(long boardId, long itemId, CancellationToken ct)
        {
            var query = @"query ($itemIds: [ID!]) {
                items(ids: $itemIds) {
                    id
                    state
                }
            }";
            var variables = new Dictionary<string, object>
            {
                ["itemIds"] = new[] { itemId.ToString() }
            };
            try
            {
                using var doc = await ExecuteGraphQLRequestAsync(query, variables, ct, "items_state", boardId, itemId, null);
                var root = doc.RootElement;
                if (!root.TryGetProperty("data", out var data) ||
                    !data.TryGetProperty("items", out var items) ||
                    items.ValueKind != JsonValueKind.Array ||
                    items.GetArrayLength() == 0)
                {
                    return null;
                }
                var item = items[0];
                if (item.TryGetProperty("state", out var stateEl))
                {
                    var s = stateEl.GetString();
                    return string.IsNullOrEmpty(s) ? null : s;
                }
                return null;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to get item state for BoardId={BoardId}, ItemId={ItemId}. Treating as unknown.", boardId, itemId);
                return null;
            }
        }

        public async Task UpdateItemAsync(long boardId, long itemId, string columnValuesJson, CancellationToken ct)
        {
            var query = @"mutation ($itemId: ID!, $boardId: ID!, $columnVals: JSON!) {
                change_multiple_column_values (item_id: $itemId, board_id: $boardId, column_values: $columnVals) {
                    id
                }
            }";

            var effectiveBoardId = boardId > 0 ? boardId : _boardId;
            var variables = new Dictionary<string, object>
            {
                ["itemId"] = itemId.ToString(),
                ["boardId"] = effectiveBoardId.ToString(),
                ["columnVals"] = columnValuesJson,
            };

            try
            {
                using var doc = await ExecuteGraphQLRequestAsync(query, variables, ct, "change_multiple_column_values", effectiveBoardId, itemId, columnValuesJson);
                var root = doc.RootElement;

                if (!root.TryGetProperty("data", out var data) ||
                    !data.TryGetProperty("change_multiple_column_values", out var change) ||
                    !change.TryGetProperty("id", out _))
                {
                    throw new MondayApiException(
                        "Monday.com unexpected response: missing change_multiple_column_values.id in response",
                        operation: "change_multiple_column_values",
                        boardId: effectiveBoardId,
                        itemId: itemId,
                        columnValuesSnippet: columnValuesJson);
                }
            }
            catch (MondayApiException)
            {
                throw;
            }
            catch (Exception ex) when (!(ex is MondayApiException))
            {
                throw new MondayApiException(
                    $"Unexpected error during change_multiple_column_values: {ex.Message}",
                    ex,
                    operation: "change_multiple_column_values",
                    boardId: effectiveBoardId,
                    itemId: itemId,
                    columnValuesSnippet: columnValuesJson);
            }
        }

        public async Task UpdateItemNameAsync(long boardId, long itemId, string name, CancellationToken ct)
        {
            // Monday.com uses the "name" column_id to update item names
            var query = @"mutation ($itemId: ID!, $boardId: ID!, $columnId: String!, $value: String!) {
                change_simple_column_value (item_id: $itemId, board_id: $boardId, column_id: $columnId, value: $value) {
                    id
                }
            }";

            var effectiveBoardId = boardId > 0 ? boardId : _boardId;
            var variables = new Dictionary<string, object>
            {
                ["itemId"] = itemId.ToString(),
                ["boardId"] = effectiveBoardId.ToString(),
                ["columnId"] = "name",
                ["value"] = name,
            };

            try
            {
                using var doc = await ExecuteGraphQLRequestAsync(query, variables, ct, "change_simple_column_value", effectiveBoardId, itemId, null);
                var root = doc.RootElement;

                if (!root.TryGetProperty("data", out var data) ||
                    !data.TryGetProperty("change_simple_column_value", out var change) ||
                    !change.TryGetProperty("id", out _))
                {
                    throw new MondayApiException(
                        "Monday.com unexpected response: missing change_simple_column_value.id in response",
                        operation: "change_simple_column_value",
                        boardId: effectiveBoardId,
                        itemId: itemId);
                }
            }
            catch (MondayApiException)
            {
                throw;
            }
            catch (Exception ex) when (!(ex is MondayApiException))
            {
                throw new MondayApiException(
                    $"Unexpected error during change_simple_column_value: {ex.Message}",
                    ex,
                    operation: "change_simple_column_value",
                    boardId: effectiveBoardId,
                    itemId: itemId);
            }
        }

        public async Task<long?> FindItemIdByColumnValueAsync(long boardId, string columnId, string columnValue, CancellationToken ct)
        {
            // Monday's items_page query_params uses ItemsQuery input type.
            // The compare_value field expects CompareValue scalar which cannot be reliably
            // passed as a GraphQL variable (causes type mismatch: "[String!]" vs "CompareValue!").
            // Fix: inline the column_id and compare_value in the query string.
            // These values come from our code (column IDs and TikNumbers), not user input.
            var escapedColumnId = EscapeGraphQLString(columnId);
            var escapedColumnValue = EscapeGraphQLString(columnValue);

            var query = $@"query ($boardId: ID!) {{
                boards (ids: [$boardId]) {{
                    items_page (limit: 1, query_params: {{
                        rules: [{{
                            column_id: ""{escapedColumnId}"",
                            compare_value: [""{escapedColumnValue}""]
                        }}]
                    }}) {{
                        items {{
                            id
                        }}
                    }}
                }}
            }}";

            var variables = new Dictionary<string, object>
            {
                ["boardId"] = boardId.ToString(),
            };

            _logger.LogDebug("FindItemIdByColumnValue: Searching board {BoardId} for column {ColumnId} = '{ColumnValue}'", boardId, columnId, columnValue);

            try
            {
                using var doc = await ExecuteGraphQLRequestAsync(query, variables, ct, "find_item_by_column", boardId, null, null);
                var root = doc.RootElement;

                if (!root.TryGetProperty("data", out var data) ||
                    !data.TryGetProperty("boards", out var boards) ||
                    boards.ValueKind != JsonValueKind.Array ||
                    boards.GetArrayLength() == 0)
                {
                    _logger.LogDebug("FindItemIdByColumnValue: No boards found in response for board {BoardId}", boardId);
                    return null;
                }

                var board = boards[0];
                if (!board.TryGetProperty("items_page", out var itemsPage) ||
                    !itemsPage.TryGetProperty("items", out var items) ||
                    items.ValueKind != JsonValueKind.Array ||
                    items.GetArrayLength() == 0)
                {
                    _logger.LogDebug("FindItemIdByColumnValue: No items found matching column {ColumnId} = '{ColumnValue}' on board {BoardId}", columnId, columnValue, boardId);
                    return null;
                }

                var firstItem = items[0];
                if (!firstItem.TryGetProperty("id", out var idElement))
                {
                    _logger.LogDebug("FindItemIdByColumnValue: Item found but missing id property for column {ColumnId} = '{ColumnValue}' on board {BoardId}", columnId, columnValue, boardId);
                    return null;
                }

                var idString = idElement.GetString();
                if (string.IsNullOrWhiteSpace(idString))
                {
                    _logger.LogDebug("FindItemIdByColumnValue: Item found but id is empty for column {ColumnId} = '{ColumnValue}' on board {BoardId}", columnId, columnValue, boardId);
                    return null;
                }

                var itemId = long.Parse(idString);
                _logger.LogInformation("FindItemIdByColumnValue: Found Monday item {ItemId} for column {ColumnId} = '{ColumnValue}' on board {BoardId}", itemId, columnId, columnValue, boardId);
                return itemId;
            }
            catch (MondayApiException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "FindItemIdByColumnValue: Search failed for column {ColumnId} = '{ColumnValue}' on board {BoardId}. Error: {Error}", columnId, columnValue, boardId, ex.Message);
                return null;
            }
        }

        /// <summary>
        /// Escapes a string for safe inline use in a GraphQL query string.
        /// Handles backslash and double-quote characters.
        /// </summary>
        private static string EscapeGraphQLString(string s)
        {
            return s.Replace("\\", "\\\\").Replace("\"", "\\\"");
        }

        public async Task<string?> GetHearingApprovalStatusAsync(long itemId, CancellationToken ct)
        {
            const string approvalColumnId = "color_mkzbmv1b";

            var query = @"query ($itemIds: [ID!], $columnIds: [String!]) {
                items(ids: $itemIds) {
                    id
                    column_values(ids: $columnIds) {
                        id
                        value
                    }
                }
            }";

            var variables = new Dictionary<string, object>
            {
                ["itemIds"] = new[] { itemId.ToString() },
                ["columnIds"] = new[] { approvalColumnId }
            };

            using var doc = await ExecuteGraphQLRequestAsync(
                query,
                variables,
                ct,
                operation: "hearing_approval_status",
                boardId: null,
                itemId: itemId);

            var root = doc.RootElement;

            if (!root.TryGetProperty("data", out var data) ||
                !data.TryGetProperty("items", out var items) ||
                items.ValueKind != JsonValueKind.Array ||
                items.GetArrayLength() == 0)
            {
                return null;
            }

            var item = items[0];
            if (!item.TryGetProperty("column_values", out var columnValues) ||
                columnValues.ValueKind != JsonValueKind.Array ||
                columnValues.GetArrayLength() == 0)
            {
                return null;
            }

            var col = columnValues[0];
            if (!col.TryGetProperty("value", out var valueElement) ||
                valueElement.ValueKind == JsonValueKind.Null)
            {
                return null;
            }

            var raw = valueElement.GetString();
            if (string.IsNullOrWhiteSpace(raw))
            {
                return null;
            }

            try
            {
                using var valDoc = JsonDocument.Parse(raw);
                var valRoot = valDoc.RootElement;
                if (valRoot.TryGetProperty("index", out var indexElement) &&
                    indexElement.ValueKind == JsonValueKind.Number &&
                    indexElement.TryGetInt32(out var idx))
                {
                    // Return the index as string: "1", "2", "5"
                    return idx.ToString(CultureInfo.InvariantCulture);
                }
            }
            catch
            {
                // Ignore parse errors and fall through to null
            }

            return null;
        }

        public async Task UpdateHearingDetailsAsync(long boardId, long itemId, string judgeName, string city, string judgeColumnId, string cityColumnId, CancellationToken ct)
        {
            var columnValues = new Dictionary<string, object>();
            if (!string.IsNullOrWhiteSpace(judgeColumnId) && !string.IsNullOrWhiteSpace(judgeName))
                columnValues[judgeColumnId] = judgeName;
            if (!string.IsNullOrWhiteSpace(cityColumnId) && !string.IsNullOrWhiteSpace(city))
                columnValues[cityColumnId] = city;
            if (columnValues.Count == 0)
                return;
            var json = JsonSerializer.Serialize(columnValues);
            _logger.LogDebug(
                "UpdateHearingDetailsAsync columnValuesJson: BoardId={BoardId}, ItemId={ItemId}, JSON={Json}",
                boardId, itemId, json);
            await UpdateItemAsync(boardId, itemId, json, ct);
        }

        public async Task UpdateHearingDateAsync(long boardId, long itemId, DateTime startDate, string dateColumnId, string hourColumnId, CancellationToken ct)
        {
            var columnValues = new Dictionary<string, object>();
            if (!string.IsNullOrWhiteSpace(dateColumnId))
                columnValues[dateColumnId] = new { date = startDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) };
            if (!string.IsNullOrWhiteSpace(hourColumnId))
                columnValues[hourColumnId] = new { hour = startDate.Hour, minute = startDate.Minute };
            if (columnValues.Count == 0)
                return;
            var json = JsonSerializer.Serialize(columnValues);
            await UpdateItemAsync(boardId, itemId, json, ct);
        }

        public async Task UpdateHearingStatusAsync(long boardId, long itemId, string label, string statusColumnId, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(statusColumnId) || string.IsNullOrWhiteSpace(label))
                return;
            var columnValues = new Dictionary<string, object> { [statusColumnId] = new { label } };
            var json = JsonSerializer.Serialize(columnValues);
            await UpdateItemAsync(boardId, itemId, json, ct);
        }

        private async Task<JsonDocument> ExecuteGraphQLRequestAsync(
            string query,
            Dictionary<string, object> variables,
            CancellationToken ct,
            string? operation = null,
            long? boardId = null,
            long? itemId = null,
            string? columnValuesJson = null)
        {
            var payload = JsonSerializer.Serialize(new { query, variables });
            var content = new StringContent(payload, Encoding.UTF8, "application/json");

            HttpResponseMessage resp;
            string body;
            try
            {
                resp = await _httpClient.PostAsync("", content, ct);
                resp.EnsureSuccessStatusCode();
                body = await resp.Content.ReadAsStringAsync(ct);
            }
            catch (HttpRequestException ex)
            {
                _logger.LogError(ex,
                    "Monday.com HTTP request failed. Operation={Operation}, BoardId={BoardId}, ItemId={ItemId}",
                    operation ?? "unknown", boardId, itemId);
                throw new MondayApiException(
                    $"Monday.com HTTP request failed: {ex.Message}",
                    ex,
                    operation: operation,
                    boardId: boardId,
                    itemId: itemId,
                    columnValuesSnippet: columnValuesJson);
            }

            JsonDocument doc;
            try
            {
                doc = JsonDocument.Parse(body);
            }
            catch (JsonException ex)
            {
                _logger.LogError(ex,
                    "Monday.com API returned invalid JSON. Operation={Operation}, BoardId={BoardId}, ItemId={ItemId}, Response={Response}",
                    operation ?? "unknown", boardId, itemId, body);
                throw new MondayApiException(
                    $"Monday.com API returned invalid JSON: {ex.Message}",
                    ex,
                    operation: operation,
                    boardId: boardId,
                    itemId: itemId,
                    columnValuesSnippet: columnValuesJson);
            }

            var root = doc.RootElement;

            if (root.TryGetProperty("errors", out var errors))
            {
                var errorJson = errors.ToString();
                doc.Dispose();

                // Try to extract a more readable error message
                string errorMessage = ExtractErrorMessage(errors, errorJson);

                _logger.LogError(
                    "Monday.com GraphQL API error. Operation={Operation}, BoardId={BoardId}, ItemId={ItemId}, Errors={Errors}",
                    operation ?? "unknown", boardId, itemId, errorJson);

                throw new MondayApiException(
                    $"Monday.com API error: {errorMessage}",
                    rawErrorJson: errorJson,
                    operation: operation,
                    boardId: boardId,
                    itemId: itemId,
                    columnValuesSnippet: columnValuesJson);
            }

            return doc;
        }

        private static string ExtractErrorMessage(JsonElement errors, string fallbackJson)
        {
            try
            {
                if (errors.ValueKind == JsonValueKind.Array && errors.GetArrayLength() > 0)
                {
                    var firstError = errors[0];
                    if (firstError.TryGetProperty("message", out var messageElement))
                    {
                        var message = messageElement.GetString();
                        if (!string.IsNullOrWhiteSpace(message))
                        {
                            // Try to get column-specific error details
                            if (firstError.TryGetProperty("extensions", out var extensions))
                            {
                                if (extensions.TryGetProperty("error_data", out var errorData))
                                {
                                    var details = new List<string> { message };
                                    
                                    if (errorData.TryGetProperty("column_id", out var columnId))
                                        details.Add($"Column ID: {columnId.GetString()}");
                                    
                                    if (errorData.TryGetProperty("column_name", out var columnName))
                                        details.Add($"Column: {columnName.GetString()}");
                                    
                                    if (errorData.TryGetProperty("column_type", out var columnType))
                                        details.Add($"Type: {columnType.GetString()}");
                                    
                                    if (errorData.TryGetProperty("column_value", out var columnValue))
                                        details.Add($"Value: {columnValue.GetString()}");
                                    
                                    return string.Join("; ", details);
                                }
                            }
                            return message;
                        }
                    }
                }
            }
            catch
            {
                // Fall through to return fallback
            }

            return fallbackJson;
        }
    }
}

