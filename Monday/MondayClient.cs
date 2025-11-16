using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using Microsoft.Extensions.Configuration;
using System.Text.Json;
using System.Net.Http.Headers;
using Microsoft.Extensions.Logging;
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

            var payload = JsonSerializer.Serialize(new { query, variables });
            var content = new StringContent(payload, Encoding.UTF8, "application/json");

            var resp = await _httpClient.PostAsync("", content, ct);
            resp.EnsureSuccessStatusCode();
            var body = await resp.Content.ReadAsStringAsync(ct);

            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.TryGetProperty("errors", out var errors))
            {
                throw new InvalidOperationException($"Monday.com API error: {errors}");
            }

            if (!root.TryGetProperty("data", out var data) ||
                !data.TryGetProperty("create_item", out var createItem) ||
                !createItem.TryGetProperty("id", out var idElement))
            {
                throw new InvalidOperationException($"Monday.com unexpected response: {body}");
            }

            var idString = idElement.GetString();
            if (string.IsNullOrWhiteSpace(idString))
            {
                throw new InvalidOperationException($"Monday.com API returned empty id. Response: {body}");
            }

            return long.Parse(idString);
        }

        public async Task UpdateItemAsync(long boardId, long itemId, string columnValuesJson, CancellationToken ct)
        {
            var query = @"mutation ($itemId: ID!, $boardId: ID!, $columnVals: JSON!) {
                change_multiple_column_values (item_id: $itemId, board_id: $boardId, column_values: $columnVals) {
                    id
                }
            }";

            var variables = new Dictionary<string, object>
            {
                ["itemId"] = itemId.ToString(),
                ["boardId"] = (boardId > 0 ? boardId : _boardId).ToString(),
                ["columnVals"] = columnValuesJson,
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
                throw new InvalidOperationException($"Monday.com API error: {errors}");
            }

            if (!root.TryGetProperty("data", out var data) ||
                !data.TryGetProperty("change_multiple_column_values", out var change) ||
                !change.TryGetProperty("id", out _))
            {
                throw new InvalidOperationException($"Monday.com unexpected response: {body}");
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

            var variables = new Dictionary<string, object>
            {
                ["itemId"] = itemId.ToString(),
                ["boardId"] = (boardId > 0 ? boardId : _boardId).ToString(),
                ["columnId"] = "name",
                ["value"] = name,
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
                throw new InvalidOperationException($"Monday.com API error: {errors}");
            }

            if (!root.TryGetProperty("data", out var data) ||
                !data.TryGetProperty("change_simple_column_value", out var change) ||
                !change.TryGetProperty("id", out _))
            {
                throw new InvalidOperationException($"Monday.com unexpected response: {body}");
            }
        }
    }
}

