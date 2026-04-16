using System.Data;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Odmon.Worker.Configuration;
using Odmon.Worker.Data;
using Odmon.Worker.Models;
using Odmon.Worker.OdcanitAccess;
using Odmon.Worker.Voicenter;

namespace Odmon.Worker.Services
{
    public sealed class VoicenterCallSummaryService
    {
        private readonly VoicenterApiClient _api;
        private readonly IOdcanitWriter _odcanitWriter;
        private readonly IntegrationDbContext _integrationDb;
        private readonly OdcanitDbContext _odcanitDb;
        private readonly VoicenterCallSummarySettings _settings;
        private readonly ILogger<VoicenterCallSummaryService> _logger;

        internal const string SourceKind = "VoicenterCall";

        private static readonly string[] WitnessFieldNames = ["סלולרי עד"];
        private static readonly string[] ThirdPartyFieldNames = ["נייד צד ג'", "נייד צד ג", "Third-party driver: phone"];

        private static readonly Dictionary<string, string> StatusMap = new(StringComparer.OrdinalIgnoreCase)
        {
            ["ANSWER"] = "נענתה",
            ["BUSY"] = "תפוס",
            ["NOANSWER"] = "לא נענתה",
            ["ABANDONE"] = "ננטשה",
            ["TIMEOUT"] = "זמן המתנה עבר",
            ["CANCEL"] = "בוטלה",
        };

        public VoicenterCallSummaryService(
            VoicenterApiClient api,
            IOdcanitWriter odcanitWriter,
            IntegrationDbContext integrationDb,
            OdcanitDbContext odcanitDb,
            IOptions<VoicenterCallSummarySettings> settings,
            ILogger<VoicenterCallSummaryService> logger)
        {
            _api = api;
            _odcanitWriter = odcanitWriter;
            _integrationDb = integrationDb;
            _odcanitDb = odcanitDb;
            _settings = settings.Value;
            _logger = logger;
        }

        public async Task<VoicenterRunResult> RunAsync(string code, string bearerToken, CancellationToken ct)
        {
            var result = new VoicenterRunResult();
            var nowUtc = DateTime.UtcNow;
            var fromUtc = nowUtc.AddHours(-_settings.LookbackHours);

            if (_settings.TestMode && !string.IsNullOrWhiteSpace(_settings.TestCallId))
            {
                _logger.LogInformation("VOICENTER | TEST MODE | Processing single CallID={CallId}", _settings.TestCallId);
                var detail = await _api.FetchCallDetailAsync(bearerToken, _settings.TestCallId, ct);
                if (detail == null)
                {
                    _logger.LogWarning("VOICENTER | TEST MODE | No detail returned for CallID={CallId}", _settings.TestCallId);
                    return result;
                }
                result.DetailsFetched = 1;
                await ProcessCallDetailAsync(detail, result, ct);
                return result;
            }

            _logger.LogInformation("VOICENTER | Fetching CDR list | From={From}, To={To}", fromUtc, nowUtc);
            var cdrList = await _api.FetchCdrListAsync(code, fromUtc, nowUtc, ct);
            result.Fetched = cdrList.Count;
            _logger.LogInformation("VOICENTER | Fetched {Count} CDR entries", cdrList.Count);

            foreach (var cdr in cdrList)
            {
                ct.ThrowIfCancellationRequested();

                if (string.IsNullOrWhiteSpace(cdr.CallID))
                    continue;

                if (_settings.OnlyAnsweredCalls &&
                    !string.Equals(cdr.DialStatus, "ANSWER", StringComparison.OrdinalIgnoreCase))
                    continue;

                if (_settings.MinimumDurationSeconds > 0 && cdr.Duration < _settings.MinimumDurationSeconds)
                    continue;

                try
                {
                    if (_settings.ThrottleMs > 0)
                        await Task.Delay(_settings.ThrottleMs, ct);

                    var detail = await _api.FetchCallDetailAsync(bearerToken, cdr.CallID, ct);
                    if (detail == null)
                        continue;
                    result.DetailsFetched++;

                    await ProcessCallDetailAsync(detail, result, ct);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    result.Failed++;
                    if (result.FailedCallIds.Count < 20)
                        result.FailedCallIds.Add(cdr.CallID!);
                    _logger.LogError(ex, "VOICENTER | Per-call failure | CallID={CallId}", cdr.CallID);
                }
            }

            _logger.LogInformation(
                "VOICENTER | Run complete | Fetched={Fetched}, Details={Details}, Written={Written}, NoAI={NoAi}, NoMatch={NoMatch}, Dup={Dup}, Failed={Failed}",
                result.Fetched, result.DetailsFetched, result.Written, result.SkippedNoAi,
                result.SkippedNoMatch, result.SkippedDuplicate, result.Failed);

            return result;
        }

        private async Task ProcessCallDetailAsync(
            VoicenterCallDetail detail, VoicenterRunResult result, CancellationToken ct)
        {
            var hasSummary = !string.IsNullOrWhiteSpace(detail.AiSummary);
            if (_settings.TestMode)
            {
                _logger.LogInformation(
                    "VOICENTER | TEST DIAG | CallID={CallId}, SummaryFound={SummaryFound}, SummaryLength={SummaryLength}",
                    detail.CallId, hasSummary, detail.AiSummary?.Length ?? 0);
            }

            if (!hasSummary)
            {
                result.SkippedNoAi++;
                _logger.LogDebug("VOICENTER | Skip no AI summary | CallID={CallId}", detail.CallId);
                return;
            }

            var phone = ResolveCallPhone(detail);
            var normalizedPhone = NormalizeIsraeliPhone(phone);
            if (string.IsNullOrWhiteSpace(normalizedPhone))
            {
                result.SkippedNoMatch++;
                _logger.LogDebug("VOICENTER | Skip no normalizable phone | CallID={CallId}", detail.CallId);
                return;
            }

            var matches = await FindCasesByPhoneAsync(normalizedPhone, ct);
            if (matches.Count == 0)
            {
                result.SkippedNoMatch++;
                _logger.LogDebug("VOICENTER | Skip no case match | CallID={CallId}, Phone={Phone}",
                    detail.CallId, MaskPhone(normalizedPhone));
                return;
            }

            _logger.LogInformation("VOICENTER | Matched {Count} case(s) | CallID={CallId}, Phone={Phone}",
                matches.Count, detail.CallId, MaskPhone(normalizedPhone));

            var annexText = BuildAnnexText(detail);
            var callIdHash = CallIdToSourceItemId(detail.CallId);

            foreach (var match in matches)
            {
                var alreadyWritten = await _integrationDb.NispahWriteLogs
                    .AsNoTracking()
                    .AnyAsync(w => w.SourceKind == SourceKind
                                   && w.SourceItemId == callIdHash
                                   && w.TikCounter == match.TikCounter
                                   && !w.Failed, ct);
                if (alreadyWritten)
                {
                    result.SkippedDuplicate++;
                    _logger.LogDebug("VOICENTER | Skip duplicate | CallID={CallId}, TikCounter={TikCounter}",
                        detail.CallId, match.TikCounter);
                    continue;
                }

                await WriteAnnexForCaseAsync(detail, match, annexText, callIdHash, result, ct);
            }
        }

        private async Task WriteAnnexForCaseAsync(
            VoicenterCallDetail detail, CasePhoneMatch match,
            string annexText, long callIdHash,
            VoicenterRunResult result, CancellationToken ct)
        {
            var nowUtc = DateTime.UtcNow;
            NispahWriteLog writeLog;
            try
            {
                var stubCase = new OdcanitCase { TikCounter = match.TikCounter, TikNumber = match.TikNumber };
                await _odcanitWriter.AppendNispahAsync(stubCase, nowUtc, _settings.NispahTypeName, annexText, ct);

                writeLog = BuildWriteLog(match.TikCounter, match.TikNumber, callIdHash,
                    detail.CallId, annexText, nowUtc, failed: false);
                _logger.LogInformation(
                    "VOICENTER | Annex written | CallID={CallId}, TikNumber={TikNumber}, TikCounter={TikCounter}, MatchedField={Field}",
                    detail.CallId, match.TikNumber, match.TikCounter, match.MatchedField);
            }
            catch (Exception ex)
            {
                writeLog = BuildWriteLog(match.TikCounter, match.TikNumber, callIdHash,
                    detail.CallId, annexText, nowUtc, failed: true, ex.Message);
                _integrationDb.NispahWriteLogs.Add(writeLog);
                try { await _integrationDb.SaveChangesAsync(ct); }
                catch (Exception logEx) { _logger.LogWarning(logEx, "VOICENTER | Failed to persist failure NispahWriteLog"); }

                _logger.LogError(ex, "VOICENTER | Annex write FAILED | CallID={CallId}, TikNumber={TikNumber}",
                    detail.CallId, match.TikNumber);
                result.Failed++;
                if (result.FailedCallIds.Count < 20 && !result.FailedCallIds.Contains(detail.CallId))
                    result.FailedCallIds.Add(detail.CallId);
                return;
            }

            _integrationDb.NispahWriteLogs.Add(writeLog);
            await _integrationDb.SaveChangesAsync(ct);
            result.Written++;
        }

        // ─── Phone resolution ───

        private static string? ResolveCallPhone(VoicenterCallDetail d)
        {
            if (!string.IsNullOrWhiteSpace(d.ClientPhone)) return d.ClientPhone;
            if (!string.IsNullOrWhiteSpace(d.TargetNo)) return d.TargetNo;
            if (!string.IsNullOrWhiteSpace(d.CallerNo)) return d.CallerNo;
            return null;
        }

        internal static string? NormalizeIsraeliPhone(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return null;
            var digits = new string(raw.Where(char.IsDigit).ToArray());
            if (digits.Length == 0) return null;

            if (digits.StartsWith("972") && digits.Length > 3)
                digits = "0" + digits[3..];
            else if (!digits.StartsWith("0") && digits.Length == 9)
                digits = "0" + digits;

            if (digits.Length < 9 || digits.Length > 11) return null;
            return digits;
        }

        // ─── Odcanit phone matching (direct SQL against vwExportToOuterSystems_UserData) ───

        private async Task<List<CasePhoneMatch>> FindCasesByPhoneAsync(string normalizedPhone, CancellationToken ct)
        {
            var allFieldNames = WitnessFieldNames.Concat(ThirdPartyFieldNames).ToArray();
            var matches = new List<CasePhoneMatch>();

            var connection = _odcanitDb.Database.GetDbConnection();
            if (connection.State != ConnectionState.Open)
                await connection.OpenAsync(ct);

            await using var command = connection.CreateCommand();
            command.CommandTimeout = 30;

            var paramNames = new string[allFieldNames.Length];
            for (int i = 0; i < allFieldNames.Length; i++)
            {
                paramNames[i] = $"@fn{i}";
                command.Parameters.Add(new SqlParameter(paramNames[i], SqlDbType.NVarChar, 200) { Value = allFieldNames[i] });
            }

            command.CommandText = $@"
SELECT DISTINCT f.TikCounter, f.TikNumber, ud.FieldName, ud.strData
FROM vwExportToOuterSystems_UserData ud WITH (NOLOCK)
INNER JOIN vwExportToOuterSystems_Files f WITH (NOLOCK) ON f.TikCounter = ud.TikCounter
WHERE ud.FieldName IN ({string.Join(", ", paramNames)})
  AND ud.strData IS NOT NULL AND LEN(LTRIM(RTRIM(ud.strData))) > 0";

            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var tikCounter = reader.GetInt32(0);
                var tikNumber = reader.IsDBNull(1) ? "" : reader.GetString(1);
                var fieldName = reader.IsDBNull(2) ? "" : reader.GetString(2);
                var phoneValue = reader.IsDBNull(3) ? "" : reader.GetString(3);

                var normalizedDbPhone = NormalizeIsraeliPhone(phoneValue);
                if (normalizedDbPhone == null || normalizedDbPhone != normalizedPhone)
                    continue;

                var matchedField = WitnessFieldNames.Contains(fieldName) ? "WitnessMobile" : "ThirdPartyDriverMobile";
                if (!matches.Any(m => m.TikCounter == tikCounter))
                {
                    matches.Add(new CasePhoneMatch
                    {
                        TikCounter = tikCounter,
                        TikNumber = tikNumber,
                        MatchedField = matchedField,
                    });
                }
            }

            return matches;
        }

        // ─── Annex text ───

        private string BuildAnnexText(VoicenterCallDetail detail)
        {
            var israelTz = SyncService.GetIsraelTimeZone();
            var callTimeIsrael = detail.CallTime.HasValue
                ? TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(detail.CallTime.Value, DateTimeKind.Utc), israelTz)
                : (DateTime?)null;

            var dateStr = callTimeIsrael?.ToString("dd/MM/yyyy") ?? "לא ידוע";
            var timeStr = callTimeIsrael?.ToString("HH:mm") ?? "לא ידוע";
            var durationStr = FormatDuration(detail.DurationSeconds);
            var statusStr = MapStatus(detail.DialStatus);

            return $"""
בוצעה שיחה ללקוח

תאריך: {dateStr}
שעה: {timeStr}
משך: {durationStr}
סטטוס: {statusStr}

סיכום:
{detail.AiSummary?.Trim()}
""";
        }

        private static string FormatDuration(int seconds)
        {
            if (seconds <= 0) return "0:00";
            var m = seconds / 60;
            var s = seconds % 60;
            return $"{m}:{s:D2}";
        }

        private static string MapStatus(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return "לא ידוע";
            return StatusMap.TryGetValue(raw, out var hebrew) ? hebrew : raw;
        }

        // ─── NispahWriteLog helpers ───

        /// <summary>Deterministic long hash of a string CallID for NispahWriteLog.SourceItemId.</summary>
        internal static long CallIdToSourceItemId(string callId)
        {
            var hash = SHA256.HashData(Encoding.UTF8.GetBytes(callId));
            return BitConverter.ToInt64(hash, 0);
        }

        private NispahWriteLog BuildWriteLog(
            int tikCounter, string tikNumber, long callIdHash,
            string callId, string annexText, DateTime nowUtc,
            bool failed, string? errorMessage = null)
        {
            return new NispahWriteLog
            {
                TikCounter = tikCounter,
                TikVisualId = tikNumber,
                NispahType = _settings.NispahTypeName,
                SourceKind = SourceKind,
                SourceItemId = callIdHash,
                InfoHash = ComputeSha256(annexText),
                CreatedAtUtc = nowUtc,
                Failed = failed,
                ErrorMessage = failed
                    ? (errorMessage?.Length > 2000 ? errorMessage[..2000] : errorMessage)
                    : $"CallID={callId}",
            };
        }

        internal static string ComputeSha256(string input)
        {
            var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(input));
            return Convert.ToHexString(bytes).ToLowerInvariant();
        }

        private static string MaskPhone(string phone)
        {
            if (phone.Length <= 4) return "****";
            return phone[..^4] + "****";
        }
    }
}
