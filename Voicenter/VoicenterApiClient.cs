using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Odmon.Worker.Voicenter
{
    public sealed class VoicenterApiClient
    {
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly ILogger<VoicenterApiClient> _logger;

        private static readonly string[] CdrFields =
            ["CallerNumber", "TargetNumber", "Date", "Duration", "CallID", "Type", "DialStatus", "RecordURL"];

        public VoicenterApiClient(IHttpClientFactory httpClientFactory, ILogger<VoicenterApiClient> logger)
        {
            _httpClientFactory = httpClientFactory;
            _logger = logger;
        }

        /// <summary>Fetch CDR list. Auth: body code only — no Bearer token.</summary>
        public async Task<List<VoicenterCdrEntry>> FetchCdrListAsync(
            string code, DateTime fromUtc, DateTime toUtc, CancellationToken ct)
        {
            var client = _httpClientFactory.CreateClient("VoicenterCdr");
            var body = new
            {
                code,
                search = new
                {
                    fromdate = fromUtc.ToString("yyyy-MM-ddTHH:mm:ssZ"),
                    todate = toUtc.ToString("yyyy-MM-ddTHH:mm:ssZ"),
                },
                fields = CdrFields,
            };

            var json = JsonSerializer.Serialize(body, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
            using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.voicenter.com/hub/cdr/")
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            };

            using var response = await client.SendAsync(request, ct);
            response.EnsureSuccessStatusCode();
            var responseJson = await response.Content.ReadAsStringAsync(ct);

            return ParseCdrResponse(responseJson);
        }

        /// <summary>Fetch call detail. Auth: Bearer token only — no body code.</summary>
        public async Task<VoicenterCallDetail?> FetchCallDetailAsync(
            string bearerToken, string callId, CancellationToken ct)
        {
            var client = _httpClientFactory.CreateClient("VoicenterDetail");
            using var request = new HttpRequestMessage(HttpMethod.Get,
                $"https://api-manager.voicenter.co/api-manager-v1/Call/History/{callId}");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);

            using var response = await client.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("VOICENTER | Call detail fetch failed | CallID={CallId}, Status={Status}",
                    callId, (int)response.StatusCode);
                return null;
            }

            var responseJson = await response.Content.ReadAsStringAsync(ct);
            return ParseCallDetailResponse(responseJson, callId);
        }

        private List<VoicenterCdrEntry> ParseCdrResponse(string json)
        {
            var result = new List<VoicenterCdrEntry>();
            try
            {
                using var doc = JsonDocument.Parse(json);

                JsonElement dataArray;
                if (doc.RootElement.ValueKind == JsonValueKind.Array)
                    dataArray = doc.RootElement;
                else if (doc.RootElement.TryGetProperty("data", out var d) && d.ValueKind == JsonValueKind.Array)
                    dataArray = d;
                else
                {
                    _logger.LogWarning("VOICENTER | CDR response has unexpected structure; RootKind={Kind}",
                        doc.RootElement.ValueKind);
                    return result;
                }

                foreach (var item in dataArray.EnumerateArray())
                {
                    var entry = new VoicenterCdrEntry
                    {
                        CallID = GetStringProp(item, "CallID"),
                        CallerNumber = GetStringProp(item, "CallerNumber"),
                        TargetNumber = GetStringProp(item, "TargetNumber"),
                        Date = GetStringProp(item, "Date"),
                        Duration = GetIntProp(item, "Duration"),
                        Type = GetStringProp(item, "Type"),
                        DialStatus = GetStringProp(item, "DialStatus"),
                        RecordURL = GetStringProp(item, "RecordURL"),
                    };
                    if (!string.IsNullOrWhiteSpace(entry.CallID))
                        result.Add(entry);
                }
            }
            catch (JsonException ex)
            {
                _logger.LogWarning(ex, "VOICENTER | Failed to parse CDR response");
            }
            return result;
        }

        private VoicenterCallDetail? ParseCallDetailResponse(string json, string callId)
        {
            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                JsonElement cdr;
                if (root.TryGetProperty("Data", out var dataObj) && dataObj.TryGetProperty("cdr_data", out cdr))
                { /* nested Data.cdr_data */ }
                else if (root.TryGetProperty("data", out var dataLower) && dataLower.TryGetProperty("cdr_data", out cdr))
                { /* lowercase variant */ }
                else if (root.TryGetProperty("cdr_data", out cdr))
                { /* flat */ }
                else
                {
                    _logger.LogDebug("VOICENTER | No cdr_data in detail response for CallID={CallId}", callId);
                    return null;
                }

                // Data.ai_data (phone + summary live here)
                JsonElement aiData = default;
                if (dataObj.ValueKind == JsonValueKind.Object)
                    dataObj.TryGetProperty("ai_data", out aiData);

                var detail = new VoicenterCallDetail
                {
                    CallId = callId,
                    UniqueId = GetStringProp(cdr, "iVR_unique_id"),
                    DurationSeconds = GetIntProp(cdr, "sec_total"),
                    DialStatus = GetStringProp(cdr, "dialstatus_name"),
                    ClientPhone = GetStringProp(aiData, "client_phone") ?? GetStringProp(cdr, "client_phone"),
                    TargetNo = GetStringProp(cdr, "target_no"),
                    CallerNo = GetStringProp(cdr, "caller_no"),
                };

                if (DateTime.TryParse(GetStringProp(cdr, "cdr_time"), out var parsedTime))
                    detail.CallTime = DateTime.SpecifyKind(parsedTime, DateTimeKind.Utc);

                detail.AiSummary = ExtractAiSummary(aiData, root);

                return detail;
            }
            catch (JsonException ex)
            {
                _logger.LogWarning(ex, "VOICENTER | Failed to parse call detail for CallID={CallId}", callId);
                return null;
            }
        }

        /// <summary>
        /// Extract AI summary. Primary: ai_data.insights.summary (confirmed Voicenter payload structure).
        /// aiData is already resolved as Data.ai_data by the caller.
        /// </summary>
        private static string? ExtractAiSummary(JsonElement aiData, JsonElement root)
        {
            // Primary: ai_data.insights.summary (pre-resolved from Data.ai_data)
            if (aiData.ValueKind == JsonValueKind.Object
                && aiData.TryGetProperty("insights", out var insights)
                && TryGetString(insights, "summary", out var primary))
                return primary;

            // Fallback: scan common flat locations on root
            string[] summaryKeys = ["summary", "ai_summary", "Summary"];
            foreach (var key in summaryKeys)
                if (TryGetString(root, key, out var v)) return v;

            if (root.TryGetProperty("Data", out var data) || root.TryGetProperty("data", out data))
            {
                foreach (var key in summaryKeys)
                    if (TryGetString(data, key, out var v)) return v;
            }

            return null;
        }

        private static bool TryGetString(JsonElement el, string key, out string? value)
        {
            value = null;
            if (el.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String)
            {
                value = v.GetString();
                return !string.IsNullOrWhiteSpace(value);
            }
            return false;
        }

        private static string? GetStringProp(JsonElement el, string name)
        {
            if (el.TryGetProperty(name, out var v))
            {
                return v.ValueKind switch
                {
                    JsonValueKind.String => v.GetString(),
                    JsonValueKind.Number => v.GetRawText(),
                    _ => null
                };
            }
            return null;
        }

        private static int GetIntProp(JsonElement el, string name)
        {
            if (el.TryGetProperty(name, out var v))
            {
                if (v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n)) return n;
                if (v.ValueKind == JsonValueKind.String && int.TryParse(v.GetString(), out var parsed)) return parsed;
            }
            return 0;
        }

        private static bool GetBoolProp(JsonElement el, string name)
        {
            if (el.TryGetProperty(name, out var v))
            {
                if (v.ValueKind is JsonValueKind.True) return true;
                if (v.ValueKind is JsonValueKind.False) return false;
                if (v.ValueKind == JsonValueKind.Number) return v.GetInt32() != 0;
                if (v.ValueKind == JsonValueKind.String)
                {
                    var s = v.GetString();
                    return s is "1" or "true" or "True";
                }
            }
            return false;
        }
    }
}
