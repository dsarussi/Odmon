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

                var detail = new VoicenterCallDetail
                {
                    CallId = callId,
                    UniqueId = GetStringProp(cdr, "iVR_unique_id"),
                    DurationSeconds = GetIntProp(cdr, "sec_total"),
                    DialStatus = GetStringProp(cdr, "dialstatus_name"),
                    ClientPhone = GetStringProp(cdr, "client_phone"),
                    TargetNo = GetStringProp(cdr, "target_no"),
                    CallerNo = GetStringProp(cdr, "caller_no"),
                    AiExists = GetBoolProp(cdr, "AiExists"),
                };

                if (DateTime.TryParse(GetStringProp(cdr, "cdr_time"), out var parsedTime))
                    detail.CallTime = DateTime.SpecifyKind(parsedTime, DateTimeKind.Utc);

                detail.AiSummary = ExtractAiSummary(root);

                return detail;
            }
            catch (JsonException ex)
            {
                _logger.LogWarning(ex, "VOICENTER | Failed to parse call detail for CallID={CallId}", callId);
                return null;
            }
        }

        private static string? ExtractAiSummary(JsonElement root)
        {
            // Try multiple known locations for the AI summary field
            string[] summaryPaths = ["summary", "ai_summary", "Summary", "AiSummary"];

            foreach (var path in summaryPaths)
            {
                if (root.TryGetProperty(path, out var val) && val.ValueKind == JsonValueKind.String)
                    return val.GetString();
            }

            if (root.TryGetProperty("Data", out var data) || root.TryGetProperty("data", out data))
            {
                foreach (var path in summaryPaths)
                {
                    if (data.TryGetProperty(path, out var val) && val.ValueKind == JsonValueKind.String)
                        return val.GetString();
                }

                if (data.TryGetProperty("cdr_data", out var cdr))
                {
                    foreach (var path in summaryPaths)
                    {
                        if (cdr.TryGetProperty(path, out var val) && val.ValueKind == JsonValueKind.String)
                            return val.GetString();
                    }
                }

                if (data.TryGetProperty("ai_data", out var ai))
                {
                    foreach (var path in summaryPaths)
                    {
                        if (ai.TryGetProperty(path, out var val) && val.ValueKind == JsonValueKind.String)
                            return val.GetString();
                    }
                }
            }

            return null;
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
