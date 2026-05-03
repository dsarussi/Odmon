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

        // Substrings (case-insensitive) that indicate a quota / usage-limit response from Voicenter.
        private static readonly string[] QuotaPhrases =
        [
            "weekly usage limit",
            "usage limit",
            "quota",
            "limit reached",
        ];

        public VoicenterApiClient(IHttpClientFactory httpClientFactory, ILogger<VoicenterApiClient> logger)
        {
            _httpClientFactory = httpClientFactory;
            _logger = logger;
        }

        /// <summary>Fetch CDR list. Auth: body code only — no Bearer token.</summary>
        public async Task<VoicenterApiResult<List<VoicenterCdrEntry>>> FetchCdrListAsync(
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
            var status = (int)response.StatusCode;
            var responseJson = await response.Content.ReadAsStringAsync(ct);

            if (LooksLikeQuotaResponse(status, responseJson))
            {
                var snippet = Snippet(responseJson);
                _logger.LogError(
                    "VOICENTER | API LIMIT EXCEEDED | Endpoint=CdrList, Status={Status}, Description={Body}",
                    status, snippet);
                return VoicenterApiResult<List<VoicenterCdrEntry>>.Quota(status, "CdrList quota / usage-limit response", snippet);
            }

            if (!response.IsSuccessStatusCode)
            {
                var snippet = Snippet(responseJson);
                _logger.LogWarning(
                    "VOICENTER | CDR list fetch failed | Status={Status}, Body={Body}",
                    status, snippet);
                return VoicenterApiResult<List<VoicenterCdrEntry>>.Failure(status, $"HTTP {status}", snippet);
            }

            var entries = ParseCdrResponse(responseJson);
            return VoicenterApiResult<List<VoicenterCdrEntry>>.Ok(entries, status);
        }

        /// <summary>
        /// Fetch call detail. Auth: Bearer token only — no body code.
        /// Throws <see cref="VoicenterQuotaExceededException"/> when Voicenter returns a usage-limit response,
        /// so the caller can stop further detail requests for the cycle.
        /// </summary>
        public async Task<VoicenterApiResult<VoicenterCallDetail?>> FetchCallDetailAsync(
            string bearerToken, string callId, CancellationToken ct)
        {
            var client = _httpClientFactory.CreateClient("VoicenterDetail");
            using var request = new HttpRequestMessage(HttpMethod.Get,
                $"https://api-manager.voicenter.co/api-manager-v1/Call/History/{callId}");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);

            using var response = await client.SendAsync(request, ct);
            var status = (int)response.StatusCode;
            var responseJson = await response.Content.ReadAsStringAsync(ct);

            if (LooksLikeQuotaResponse(status, responseJson))
            {
                var snippet = Snippet(responseJson);
                _logger.LogError(
                    "VOICENTER | API LIMIT EXCEEDED | Endpoint=CallHistoryDetail, Status={Status}, CallID={CallId}, Description={Body}",
                    status, callId, snippet);
                throw new VoicenterQuotaExceededException(
                    VoicenterEndpointTypeStrings.CallHistoryDetail,
                    status,
                    callId,
                    snippet,
                    $"Voicenter CallHistoryDetail quota exceeded (HTTP {status}) for CallID={callId}");
            }

            if (!response.IsSuccessStatusCode)
            {
                var snippet = Snippet(responseJson);
                _logger.LogWarning(
                    "VOICENTER | Call detail fetch failed | CallID={CallId}, Status={Status}, Body={Body}",
                    callId, status, snippet);
                return VoicenterApiResult<VoicenterCallDetail?>.Failure(status, $"HTTP {status}", snippet);
            }

            var detail = ParseCallDetailResponse(responseJson, callId);
            return VoicenterApiResult<VoicenterCallDetail?>.Ok(detail, status);
        }

        // ─── Quota / body inspection helpers ───

        private static bool LooksLikeQuotaResponse(int status, string body)
        {
            if (status == 401) return true;
            if (string.IsNullOrEmpty(body)) return false;
            foreach (var phrase in QuotaPhrases)
            {
                if (body.Contains(phrase, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        private static string Snippet(string body)
        {
            if (string.IsNullOrEmpty(body)) return string.Empty;
            return body.Length <= 400 ? body : body[..400] + "…";
        }

        // ─── Response parsing ───

        private List<VoicenterCdrEntry> ParseCdrResponse(string json)
        {
            var result = new List<VoicenterCdrEntry>();
            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                JsonElement dataArray;
                if (root.TryGetProperty("CDR_LIST", out var cdrList) && cdrList.ValueKind == JsonValueKind.Array)
                    dataArray = cdrList;
                else if (root.ValueKind == JsonValueKind.Array)
                    dataArray = root;
                else if (root.TryGetProperty("data", out var d) && d.ValueKind == JsonValueKind.Array)
                    dataArray = d;
                else
                {
                    _logger.LogWarning("VOICENTER | CDR response has unexpected structure; RootKind={Kind}, HasCDR_LIST=false",
                        root.ValueKind);
                    return result;
                }

                _logger.LogInformation(
                    "VOICENTER | CDR parsed | RootKind={RootKind}, CDR_LIST_Found={Found}, Count={Count}",
                    root.ValueKind, root.TryGetProperty("CDR_LIST", out _), dataArray.GetArrayLength());

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
                    // Keep entries even when CallID is missing/empty so the service-level
                    // counters can attribute them to SkippedMissingCallId.
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
                if (root.ValueKind != JsonValueKind.Object)
                {
                    _logger.LogWarning("VOICENTER | Skipping CallID={CallId} due to unexpected JSON shape in call detail response (RootKind={Kind})",
                        callId, root.ValueKind);
                    return null;
                }

                JsonElement dataObj = default;
                JsonElement cdr = default;
                bool hasCdr = false;

                if (root.TryGetProperty("Data", out dataObj) && dataObj.ValueKind == JsonValueKind.Object
                    && dataObj.TryGetProperty("cdr_data", out cdr) && cdr.ValueKind == JsonValueKind.Object)
                    hasCdr = true;
                else if (root.TryGetProperty("data", out dataObj) && dataObj.ValueKind == JsonValueKind.Object
                    && dataObj.TryGetProperty("cdr_data", out cdr) && cdr.ValueKind == JsonValueKind.Object)
                    hasCdr = true;
                else if (root.TryGetProperty("cdr_data", out cdr) && cdr.ValueKind == JsonValueKind.Object)
                    hasCdr = true;

                if (!hasCdr)
                {
                    _logger.LogWarning("VOICENTER | Skipping CallID={CallId} due to unexpected JSON shape in call detail response (no cdr_data object)",
                        callId);
                    return null;
                }

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
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "VOICENTER | Skipping CallID={CallId} due to unexpected JSON shape in call detail response", callId);
                return null;
            }
        }

        /// <summary>
        /// Extract AI summary. Primary: ai_data.insights.summary (confirmed Voicenter payload structure).
        /// aiData is already resolved as Data.ai_data by the caller.
        /// </summary>
        private static string? ExtractAiSummary(JsonElement aiData, JsonElement root)
        {
            if (aiData.ValueKind == JsonValueKind.Object
                && aiData.TryGetProperty("insights", out var insights)
                && TryGetString(insights, "summary", out var primary))
                return primary;

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
            if (el.ValueKind != JsonValueKind.Object) return false;
            if (el.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String)
            {
                value = v.GetString();
                return !string.IsNullOrWhiteSpace(value);
            }
            return false;
        }

        private static string? GetStringProp(JsonElement el, string name)
        {
            if (el.ValueKind != JsonValueKind.Object) return null;
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
            if (el.ValueKind != JsonValueKind.Object) return 0;
            if (el.TryGetProperty(name, out var v))
            {
                if (v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n)) return n;
                if (v.ValueKind == JsonValueKind.String && int.TryParse(v.GetString(), out var parsed)) return parsed;
            }
            return 0;
        }
    }

    /// <summary>String constants mirroring Models.VoicenterEndpointType (avoids cross-namespace dependency in this file).</summary>
    internal static class VoicenterEndpointTypeStrings
    {
        public const string CdrList = "CdrList";
        public const string CallHistoryDetail = "CallHistoryDetail";
        public const string Other = "Other";
    }
}
