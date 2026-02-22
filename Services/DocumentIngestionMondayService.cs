using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Odmon.Worker.Security;

namespace Odmon.Worker.Services
{
    public class DocumentIngestionMondayService
    {
        private readonly HttpClient _httpClient;
        private readonly ILogger<DocumentIngestionMondayService> _logger;

        public DocumentIngestionMondayService(
            HttpClient httpClient,
            IConfiguration config,
            ISecretProvider secretProvider,
            ILogger<DocumentIngestionMondayService> logger)
        {
            _httpClient = httpClient;
            _logger = logger;

            var apiToken = secretProvider.GetSecret("Monday__ApiToken");
            if (string.IsNullOrWhiteSpace(apiToken) || IsPlaceholder(apiToken))
            {
                var fallback = config["Monday:ApiToken"];
                if (!string.IsNullOrWhiteSpace(fallback) && !IsPlaceholder(fallback))
                    apiToken = fallback;
            }

            if (!string.IsNullOrWhiteSpace(apiToken) && !IsPlaceholder(apiToken))
                _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiToken);
            else
                _logger.LogError("Monday API token not configured for DocumentIngestionMondayService.");
        }

        // ───────── DTOs ─────────

        public class QuestionnaireItem
        {
            public long ItemId { get; set; }
            public string Name { get; set; } = string.Empty;
            public List<long> LinkedCaseItemIds { get; set; } = [];
            public Dictionary<string, List<FileAssetRef>> FileColumns { get; set; } = new();
        }

        public class FileAssetRef
        {
            public long AssetId { get; set; }
            public string Name { get; set; } = string.Empty;
        }

        public class AssetDownloadInfo
        {
            public long AssetId { get; set; }
            public string Name { get; set; } = string.Empty;
            public string PublicUrl { get; set; } = string.Empty;
            public string FileExtension { get; set; } = string.Empty;
            public long FileSize { get; set; }
        }

        // ───────── Fetch questionnaire items (paginated) ─────────

        public async Task<List<QuestionnaireItem>> FetchQuestionnaireItemsAsync(
            long boardId, string[] fileColumnIds, string relationColumnId, int pageLimit, CancellationToken ct)
        {
            var allColumnIds = fileColumnIds.Append(relationColumnId).ToArray();
            var columnIdsJson = JsonSerializer.Serialize(allColumnIds);

            var items = new List<QuestionnaireItem>();
            string? cursor = null;

            do
            {
                string query;
                Dictionary<string, object> variables;

                var columnValuesFragment = @"
                                    column_values(ids: $columnIds) {
                                        id
                                        value
                                        ... on BoardRelationValue {
                                            linked_item_ids
                                        }
                                    }";

                if (cursor == null)
                {
                    query = $@"query ($boardId: ID!, $columnIds: [String!], $limit: Int!) {{
                        boards(ids: [$boardId]) {{
                            items_page(limit: $limit) {{
                                cursor
                                items {{
                                    id
                                    name
                                    {columnValuesFragment}
                                }}
                            }}
                        }}
                    }}";
                    variables = new Dictionary<string, object>
                    {
                        ["boardId"] = boardId.ToString(),
                        ["columnIds"] = allColumnIds,
                        ["limit"] = pageLimit
                    };
                }
                else
                {
                    query = $@"query ($cursor: String!, $columnIds: [String!], $limit: Int!) {{
                        next_items_page(cursor: $cursor, limit: $limit) {{
                            cursor
                            items {{
                                id
                                name
                                {columnValuesFragment}
                            }}
                        }}
                    }}";
                    variables = new Dictionary<string, object>
                    {
                        ["cursor"] = cursor,
                        ["columnIds"] = allColumnIds,
                        ["limit"] = pageLimit
                    };
                }

                using var doc = await ExecuteGraphQLAsync(query, variables, ct);
                var root = doc.RootElement;

                JsonElement itemsPage;
                if (cursor == null)
                {
                    if (!root.TryGetProperty("data", out var data) ||
                        !data.TryGetProperty("boards", out var boards) ||
                        boards.ValueKind != JsonValueKind.Array || boards.GetArrayLength() == 0 ||
                        !boards[0].TryGetProperty("items_page", out itemsPage))
                        break;
                }
                else
                {
                    if (!root.TryGetProperty("data", out var data) ||
                        !data.TryGetProperty("next_items_page", out itemsPage))
                        break;
                }

                cursor = itemsPage.TryGetProperty("cursor", out var cursorEl) && cursorEl.ValueKind == JsonValueKind.String
                    ? cursorEl.GetString()
                    : null;

                if (!itemsPage.TryGetProperty("items", out var itemsArray) ||
                    itemsArray.ValueKind != JsonValueKind.Array)
                    break;

                foreach (var itemEl in itemsArray.EnumerateArray())
                {
                    var qi = ParseQuestionnaireItem(itemEl, fileColumnIds, relationColumnId);
                    if (qi != null)
                        items.Add(qi);
                }

                _logger.LogDebug("Fetched {Count} items from questionnaire board {BoardId}, cursor={Cursor}",
                    itemsArray.GetArrayLength(), boardId, cursor ?? "<end>");

            } while (!string.IsNullOrEmpty(cursor));

            _logger.LogInformation("Total questionnaire items fetched: {Count} from board {BoardId}", items.Count, boardId);
            return items;
        }

        // ───────── Get TikVisualID from a case item ─────────

        public async Task<string?> GetItemColumnTextAsync(long itemId, string columnId, CancellationToken ct)
        {
            var query = @"query ($itemIds: [ID!], $columnIds: [String!]) {
                items(ids: $itemIds) {
                    id
                    column_values(ids: $columnIds) {
                        id
                        text
                    }
                }
            }";

            var variables = new Dictionary<string, object>
            {
                ["itemIds"] = new[] { itemId.ToString() },
                ["columnIds"] = new[] { columnId }
            };

            using var doc = await ExecuteGraphQLAsync(query, variables, ct);
            var root = doc.RootElement;

            if (!root.TryGetProperty("data", out var data) ||
                !data.TryGetProperty("items", out var items) ||
                items.ValueKind != JsonValueKind.Array || items.GetArrayLength() == 0)
                return null;

            var item = items[0];
            if (!item.TryGetProperty("column_values", out var cols) ||
                cols.ValueKind != JsonValueKind.Array || cols.GetArrayLength() == 0)
                return null;

            var col = cols[0];
            if (col.TryGetProperty("text", out var textEl) && textEl.ValueKind == JsonValueKind.String)
                return textEl.GetString();

            return null;
        }

        // ───────── Get multiple column values from a single item ─────────

        public async Task<Dictionary<string, string>> GetItemColumnValuesAsync(
            long itemId, string[] columnIds, CancellationToken ct)
        {
            var query = @"query ($itemIds: [ID!], $columnIds: [String!]) {
                items(ids: $itemIds) {
                    id
                    column_values(ids: $columnIds) {
                        id
                        text
                    }
                }
            }";

            var variables = new Dictionary<string, object>
            {
                ["itemIds"] = new[] { itemId.ToString() },
                ["columnIds"] = columnIds
            };

            using var doc = await ExecuteGraphQLAsync(query, variables, ct);
            var root = doc.RootElement;

            var result = new Dictionary<string, string>(StringComparer.Ordinal);

            if (!root.TryGetProperty("data", out var data) ||
                !data.TryGetProperty("items", out var items) ||
                items.ValueKind != JsonValueKind.Array || items.GetArrayLength() == 0)
                return result;

            var item = items[0];
            if (!item.TryGetProperty("column_values", out var cols) ||
                cols.ValueKind != JsonValueKind.Array)
                return result;

            foreach (var col in cols.EnumerateArray())
            {
                var colId = col.TryGetProperty("id", out var cidEl) ? cidEl.GetString() : null;
                if (colId == null) continue;

                var text = col.TryGetProperty("text", out var textEl) && textEl.ValueKind == JsonValueKind.String
                    ? textEl.GetString()?.Trim() ?? ""
                    : "";

                result[colId] = text;
            }

            return result;
        }

        // ───────── Get asset download info ─────────

        public async Task<AssetDownloadInfo?> GetAssetDownloadInfoAsync(long assetId, CancellationToken ct)
        {
            var query = @"query ($assetIds: [ID!]!) {
                assets(ids: $assetIds) {
                    id
                    name
                    public_url
                    file_extension
                    file_size
                }
            }";

            var variables = new Dictionary<string, object>
            {
                ["assetIds"] = new[] { assetId }
            };

            using var doc = await ExecuteGraphQLAsync(query, variables, ct);
            var root = doc.RootElement;

            if (!root.TryGetProperty("data", out var data) ||
                !data.TryGetProperty("assets", out var assets) ||
                assets.ValueKind != JsonValueKind.Array || assets.GetArrayLength() == 0)
            {
                _logger.LogWarning("Asset {AssetId} not found in Monday API", assetId);
                return null;
            }

            var asset = assets[0];
            // Return URL exactly as returned by Monday; do not modify (pre-signed URLs invalidate on any change).
            var publicUrl = asset.TryGetProperty("public_url", out var urlEl) ? urlEl.GetString() : null;
            if (string.IsNullOrWhiteSpace(publicUrl))
            {
                _logger.LogWarning("Asset {AssetId} has no public_url", assetId);
                return null;
            }

            return new AssetDownloadInfo
            {
                AssetId = assetId,
                Name = asset.TryGetProperty("name", out var nameEl) ? nameEl.GetString() ?? "" : "",
                PublicUrl = publicUrl,
                FileExtension = asset.TryGetProperty("file_extension", out var extEl) ? extEl.GetString() ?? "" : "",
                FileSize = asset.TryGetProperty("file_size", out var sizeEl) && sizeEl.TryGetInt64(out var sz) ? sz : 0
            };
        }

        // ───────── Private helpers ─────────

        internal QuestionnaireItem? ParseQuestionnaireItem(
            JsonElement itemEl, string[] fileColumnIds, string relationColumnId)
        {
            var idStr = itemEl.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
            if (string.IsNullOrWhiteSpace(idStr) || !long.TryParse(idStr, out var itemId))
                return null;

            var name = itemEl.TryGetProperty("name", out var nameEl) ? nameEl.GetString() ?? "" : "";

            var qi = new QuestionnaireItem { ItemId = itemId, Name = name };

            if (!itemEl.TryGetProperty("column_values", out var colValues) ||
                colValues.ValueKind != JsonValueKind.Array)
                return qi;

            var fileColumnSet = new HashSet<string>(fileColumnIds, StringComparer.Ordinal);

            foreach (var col in colValues.EnumerateArray())
            {
                var colId = col.TryGetProperty("id", out var cidEl) ? cidEl.GetString() : null;
                if (colId == null) continue;

                if (colId == relationColumnId)
                {
                    qi.LinkedCaseItemIds = ParseLinkedItemIds(col);

                    if (qi.LinkedCaseItemIds.Count > 0)
                    {
                        _logger.LogDebug(
                            "DOCINGESTION item {ItemId} relation column {ColumnId} → linked_item_ids=[{Ids}]",
                            itemId, relationColumnId, string.Join(",", qi.LinkedCaseItemIds));
                    }
                    else
                    {
                        _logger.LogDebug(
                            "DOCINGESTION item {ItemId} relation column {ColumnId} returned no linked_item_ids",
                            itemId, relationColumnId);
                    }
                    continue;
                }

                if (fileColumnSet.Contains(colId))
                {
                    var rawValue = col.TryGetProperty("value", out var valEl) && valEl.ValueKind == JsonValueKind.String
                        ? valEl.GetString()
                        : null;

                    if (!string.IsNullOrWhiteSpace(rawValue))
                    {
                        var fileAssets = ParseFileAssets(rawValue);
                        if (fileAssets.Count > 0)
                            qi.FileColumns[colId] = fileAssets;
                    }
                }
            }

            return qi;
        }

        /// <summary>
        /// Extracts linked item IDs from a BoardRelationValue column.
        /// Primary path: reads "linked_item_ids" array from the inline fragment.
        /// Fallback: parses the "value" JSON for "linkedPulseIds" (older API format).
        /// </summary>
        internal static List<long> ParseLinkedItemIds(JsonElement columnElement)
        {
            // Primary: linked_item_ids from the BoardRelationValue inline fragment
            if (columnElement.TryGetProperty("linked_item_ids", out var linkedArr) &&
                linkedArr.ValueKind == JsonValueKind.Array)
            {
                var result = new List<long>();
                foreach (var el in linkedArr.EnumerateArray())
                {
                    if (el.ValueKind == JsonValueKind.String && long.TryParse(el.GetString(), out var id))
                        result.Add(id);
                    else if (el.ValueKind == JsonValueKind.Number && el.TryGetInt64(out var numId))
                        result.Add(numId);
                }
                if (result.Count > 0)
                    return result;
            }

            // Fallback: parse value JSON ({"linkedPulseIds":[{"linkedPulseId":123}]})
            var rawValue = columnElement.TryGetProperty("value", out var valEl) && valEl.ValueKind == JsonValueKind.String
                ? valEl.GetString()
                : null;

            if (!string.IsNullOrWhiteSpace(rawValue))
            {
                try
                {
                    using var doc = JsonDocument.Parse(rawValue);
                    if (doc.RootElement.TryGetProperty("linkedPulseIds", out var arr) &&
                        arr.ValueKind == JsonValueKind.Array)
                    {
                        var result = new List<long>();
                        foreach (var el in arr.EnumerateArray())
                        {
                            if (el.TryGetProperty("linkedPulseId", out var pid))
                            {
                                if (pid.ValueKind == JsonValueKind.Number && pid.TryGetInt64(out var id))
                                    result.Add(id);
                                else if (pid.ValueKind == JsonValueKind.String && long.TryParse(pid.GetString(), out var sid))
                                    result.Add(sid);
                            }
                        }
                        return result;
                    }
                }
                catch { /* malformed JSON */ }
            }

            return [];
        }

        private static List<FileAssetRef> ParseFileAssets(string json)
        {
            var result = new List<FileAssetRef>();
            try
            {
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("files", out var arr) &&
                    arr.ValueKind == JsonValueKind.Array)
                {
                    foreach (var el in arr.EnumerateArray())
                    {
                        long assetId = 0;
                        if (el.TryGetProperty("assetId", out var aidEl))
                        {
                            if (aidEl.ValueKind == JsonValueKind.Number) aidEl.TryGetInt64(out assetId);
                            else if (aidEl.ValueKind == JsonValueKind.String) long.TryParse(aidEl.GetString(), out assetId);
                        }

                        var name = el.TryGetProperty("name", out var nEl) ? nEl.GetString() ?? "" : "";

                        if (assetId > 0)
                            result.Add(new FileAssetRef { AssetId = assetId, Name = name });
                    }
                }
            }
            catch { /* malformed JSON, return empty */ }
            return result;
        }

        private async Task<JsonDocument> ExecuteGraphQLAsync(
            string query, Dictionary<string, object> variables, CancellationToken ct)
        {
            var payload = JsonSerializer.Serialize(new { query, variables });
            var content = new StringContent(payload, Encoding.UTF8, "application/json");

            var resp = await _httpClient.PostAsync("", content, ct);
            var body = await resp.Content.ReadAsStringAsync(ct);

            if (!resp.IsSuccessStatusCode)
            {
                _logger.LogError("Monday GraphQL request failed: HTTP {StatusCode}, Body={Body}",
                    (int)resp.StatusCode, body.Length > 500 ? body[..500] : body);
                throw new HttpRequestException($"Monday API returned HTTP {(int)resp.StatusCode}");
            }

            var doc = JsonDocument.Parse(body);

            if (doc.RootElement.TryGetProperty("errors", out var errors))
            {
                var errJson = errors.ToString();
                doc.Dispose();
                _logger.LogError("Monday GraphQL errors: {Errors}", errJson.Length > 500 ? errJson[..500] : errJson);
                throw new InvalidOperationException($"Monday GraphQL error: {errJson}");
            }

            return doc;
        }

        private static bool IsPlaceholder(string value) =>
            string.IsNullOrWhiteSpace(value) ||
            value.Contains("CHANGE_ME", StringComparison.OrdinalIgnoreCase) ||
            value.Contains("__USE_SECRET__", StringComparison.OrdinalIgnoreCase) ||
            value.Contains("__", StringComparison.Ordinal);
    }
}
