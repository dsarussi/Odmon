using System.Security.Cryptography;
using System.Text;
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
        private readonly VoicenterUsageTracker _usage;
        private readonly IVoicenterCasePhoneResolver _phoneResolver;
        private readonly VoicenterCallSummarySettings _settings;
        private readonly VoicenterBackfillSettings _backfillSettings;
        private readonly ILogger<VoicenterCallSummaryService> _logger;

        internal const string SourceKind = "VoicenterCall";

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
            VoicenterUsageTracker usage,
            IVoicenterCasePhoneResolver phoneResolver,
            IOptions<VoicenterCallSummarySettings> settings,
            IOptions<VoicenterBackfillSettings> backfillSettings,
            ILogger<VoicenterCallSummaryService> logger)
        {
            _api = api;
            _odcanitWriter = odcanitWriter;
            _integrationDb = integrationDb;
            _usage = usage;
            _phoneResolver = phoneResolver;
            _settings = settings.Value;
            _backfillSettings = backfillSettings.Value;
            _logger = logger;
        }

        public async Task<VoicenterRunResult> RunAsync(string code, string bearerToken, CancellationToken ct)
        {
            var result = new VoicenterRunResult
            {
                WeeklyCallHistoryDetailLimit = _settings.WeeklyUsageHardLimit,
                WeeklyCallHistoryDetailWarningThreshold = _settings.WeeklyUsageWarningThreshold,
            };

            // ─── Test mode (single CallID) ───
            if (_settings.TestMode && !string.IsNullOrWhiteSpace(_settings.TestCallId))
            {
                _logger.LogInformation("VOICENTER | TEST MODE | Processing single CallID={CallId}", _settings.TestCallId);
                await TryFetchAndProcessSingleAsync(bearerToken, _settings.TestCallId, result, forceRecheck: true, dryRun: false, ct);
                await FinalizeWeeklyCountersAsync(result, ct);
                return result;
            }

            // ─── Determine date range: backfill or normal lookback ───
            DateTime fromUtc, toUtc;
            bool backfillMode = _backfillSettings.Enable;
            bool dryRun = backfillMode && _backfillSettings.DryRun;
            int maxCallsCap = backfillMode ? _backfillSettings.MaxCalls : 0;
            bool forceRecheck = backfillMode && _backfillSettings.ForceRecheck;

            if (backfillMode)
            {
                if (_backfillSettings.FromUtc is null)
                {
                    _logger.LogError("VOICENTER BACKFILL | Enabled but FromUtc not set; aborting backfill cycle");
                    return result;
                }
                fromUtc = DateTime.SpecifyKind(_backfillSettings.FromUtc.Value, DateTimeKind.Utc);
                toUtc = DateTime.SpecifyKind(_backfillSettings.ToUtc ?? DateTime.UtcNow, DateTimeKind.Utc);
                result.BackfillMode = true;
                result.BackfillFromUtc = fromUtc;
                result.BackfillToUtc = toUtc;
                result.BackfillDryRun = dryRun;
                _logger.LogInformation(
                    "VOICENTER BACKFILL | Range={From:o}..{To:o}, DryRun={DryRun}, MaxCalls={MaxCalls}, ForceRecheck={Force}",
                    fromUtc, toUtc, dryRun, maxCallsCap, forceRecheck);
            }
            else
            {
                toUtc = DateTime.UtcNow;
                fromUtc = toUtc.AddHours(-_settings.LookbackHours);
                _logger.LogInformation("VOICENTER | Fetching CDR list | From={From:o}, To={To:o}", fromUtc, toUtc);
            }

            // ─── Fetch CDR list ───
            var cdrApiResult = await _api.FetchCdrListAsync(code, fromUtc, toUtc, ct);
            await _usage.RecordRequestAsync(
                VoicenterEndpointType.CdrList,
                callId: null,
                cdrApiResult.HttpStatus,
                success: cdrApiResult.Success,
                quotaExceeded: cdrApiResult.QuotaExceeded,
                errorMessage: cdrApiResult.ErrorMessage,
                correlationId: null,
                ct);
            result.CdrListRequestsThisRun++;

            if (cdrApiResult.QuotaExceeded)
            {
                result.ApiLimitExceeded = true;
                _logger.LogError("VOICENTER | CDR list quota exceeded — aborting cycle");
                await FinalizeWeeklyCountersAsync(result, ct);
                return result;
            }

            var cdrList = cdrApiResult.Data ?? [];
            result.Fetched = cdrList.Count;
            _logger.LogInformation("VOICENTER | Fetched {Count} CDR entries", cdrList.Count);

            // ─── Per-CDR processing loop ───
            int processedDetailRequests = 0;
            bool quotaHitDuringCycle = false;
            var processedCallIds = new HashSet<string>(StringComparer.Ordinal);

            foreach (var cdr in cdrList)
            {
                ct.ThrowIfCancellationRequested();

                if (maxCallsCap > 0 && processedDetailRequests >= maxCallsCap)
                {
                    _logger.LogInformation("VOICENTER BACKFILL | MaxCalls={Max} reached; stopping further detail fetches", maxCallsCap);
                    break;
                }

                // 1. Missing CallID
                if (string.IsNullOrWhiteSpace(cdr.CallID))
                {
                    result.SkippedMissingCallId++;
                    _logger.LogDebug("VOICENTER | Skip missing CallID (Date={Date}, Caller={Caller}, Target={Target})",
                        cdr.Date, cdr.CallerNumber, cdr.TargetNumber);
                    continue;
                }
                processedCallIds.Add(cdr.CallID);

                // 2. Already-quota-hit short circuit
                if (quotaHitDuringCycle)
                {
                    result.SkippedDueToQuotaExceeded++;
                    await UpsertProcessingStateAsync(cdr.CallID, VoicenterCallProcessingStatus.QuotaExceeded,
                        tikCounter: null, tikVisualId: null, lastError: "Cycle quota exceeded; detail fetch skipped", ct);
                    continue;
                }

                // 3. Pre-detail filters (answered / minimum duration)
                if (_settings.OnlyAnsweredCalls &&
                    !string.Equals(cdr.DialStatus, "ANSWER", StringComparison.OrdinalIgnoreCase))
                {
                    result.SkippedPreDetailOther++;
                    continue;
                }
                if (_settings.MinimumDurationSeconds > 0 && cdr.Duration < _settings.MinimumDurationSeconds)
                {
                    result.SkippedPreDetailOther++;
                    continue;
                }

                // 4. Already in processing state — skip detail unless forceRecheck
                var existingState = await _integrationDb.VoicenterCallProcessingStates
                    .AsNoTracking()
                    .FirstOrDefaultAsync(s => s.CallId == cdr.CallID, ct);

                if (existingState != null && ShouldSkipExistingState(existingState, forceRecheck, DateTime.UtcNow))
                {
                    if (existingState.Status == VoicenterCallProcessingStatus.Written
                        || existingState.Status == VoicenterCallProcessingStatus.Duplicate)
                    {
                        result.SkippedAlreadyProcessed++;
                        _logger.LogDebug("VOICENTER | Skip already-processed | CallID={CallId}, State={State}",
                            cdr.CallID, existingState.Status);
                    }
                    else
                    {
                        result.SkippedAlreadyProcessed++;
                        _logger.LogDebug("VOICENTER | Skip prior-terminal state | CallID={CallId}, State={State}",
                            cdr.CallID, existingState.Status);
                    }
                    await TouchLastSeenAsync(cdr.CallID, ct);
                    continue;
                }

                // 5. NispahWriteLogs proof of prior successful write — skip detail
                var alreadyWrittenInLogs = await NispahLogsHaveSuccessForCallIdAsync(cdr.CallID, ct);
                if (alreadyWrittenInLogs && !forceRecheck)
                {
                    result.SkippedDuplicateBeforeDetail++;
                    _logger.LogDebug("VOICENTER | Skip duplicate before detail (NispahWriteLogs proof) | CallID={CallId}", cdr.CallID);
                    await UpsertProcessingStateAsync(cdr.CallID, VoicenterCallProcessingStatus.Written,
                        tikCounter: null, tikVisualId: null, lastError: null, ct);
                    continue;
                }

                // 6. Throttle to be polite to Voicenter
                if (_settings.ThrottleMs > 0)
                    await Task.Delay(_settings.ThrottleMs, ct);

                // 7. Fetch call detail (this is the quota-burning call)
                VoicenterApiResult<VoicenterCallDetail?>? detailResult;
                try
                {
                    detailResult = await _api.FetchCallDetailAsync(bearerToken, cdr.CallID, ct);
                }
                catch (VoicenterQuotaExceededException qx)
                {
                    quotaHitDuringCycle = true;
                    result.ApiLimitExceeded = true;
                    result.SkippedDueToQuotaExceeded++;
                    await _usage.RecordRequestAsync(
                        VoicenterEndpointType.CallHistoryDetail,
                        cdr.CallID, qx.HttpStatus, success: false, quotaExceeded: true,
                        errorMessage: qx.Message, correlationId: null, ct);
                    await UpsertProcessingStateAsync(cdr.CallID, VoicenterCallProcessingStatus.QuotaExceeded,
                        tikCounter: null, tikVisualId: null, lastError: qx.Message, ct);
                    _logger.LogWarning("VOICENTER | Quota exceeded — stopping further CallHistoryDetail requests for this cycle");
                    continue;
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    result.DetailFetchFailed++;
                    result.Failed++;
                    if (result.FailedCallIds.Count < 20)
                        result.FailedCallIds.Add(cdr.CallID);
                    await _usage.RecordRequestAsync(
                        VoicenterEndpointType.CallHistoryDetail,
                        cdr.CallID, httpStatus: null, success: false, quotaExceeded: false,
                        errorMessage: ex.Message, correlationId: null, ct);
                    await UpsertProcessingStateAsync(cdr.CallID, VoicenterCallProcessingStatus.Failed,
                        tikCounter: null, tikVisualId: null, lastError: ex.Message, ct);
                    _logger.LogError(ex, "VOICENTER | Detail fetch threw | CallID={CallId}", cdr.CallID);
                    continue;
                }

                processedDetailRequests++;
                result.CallHistoryDetailRequestsThisRun++;
                await _usage.RecordRequestAsync(
                    VoicenterEndpointType.CallHistoryDetail,
                    cdr.CallID,
                    detailResult.HttpStatus,
                    success: detailResult.Success,
                    quotaExceeded: false,
                    errorMessage: detailResult.ErrorMessage,
                    correlationId: null,
                    ct);

                if (!detailResult.Success || detailResult.Data is null)
                {
                    result.DetailFetchFailed++;
                    await UpsertProcessingStateAsync(cdr.CallID, VoicenterCallProcessingStatus.Failed,
                        tikCounter: null, tikVisualId: null,
                        lastError: detailResult.ErrorMessage ?? "Detail fetch returned no payload", ct);
                    continue;
                }

                result.DetailsFetched++;
                await ProcessCallDetailAsync(detailResult.Data, result, dryRun, ct);
            }

            if (!quotaHitDuringCycle)
                await RetryDeferredStatesAsync(bearerToken, result, dryRun, processedCallIds, ct);

            await FinalizeWeeklyCountersAsync(result, ct);

            if (backfillMode)
            {
                _logger.LogInformation(
                    "VOICENTER BACKFILL SUMMARY | CDR={Cdr}, Details={Details}, Written={Written}, Duplicate={Dup}, NoAI={NoAi}, NoMatch={NoMatch}, Failed={Failed}, QuotaExceeded={Quota}",
                    result.Fetched, result.DetailsFetched, result.Written,
                    result.SkippedDuplicate + result.SkippedDuplicateBeforeDetail + result.SkippedAlreadyProcessed,
                    result.SkippedNoAi, result.SkippedNoMatch, result.Failed, result.SkippedDueToQuotaExceeded);
            }
            else
            {
                _logger.LogInformation(
                    "VOICENTER | Run complete | Fetched={Fetched}, Details={Details}, Written={Written}, NoAI={NoAi}, NoMatch={NoMatch}, Dup={Dup}, DupBefore={DupBefore}, AlreadyProc={Already}, MissingCallID={Missing}, PreDetailOther={PreOther}, QuotaSkipped={QuotaSkip}, DetailFail={DetailFail}, Failed={Failed}, ApiLimitExceeded={Limit}, WeeklyDetailReq={WeeklyDetail}/{Limit2}, WeeklyCdrReq={WeeklyCdr}",
                    result.Fetched, result.DetailsFetched, result.Written, result.SkippedNoAi, result.SkippedNoMatch,
                    result.SkippedDuplicate, result.SkippedDuplicateBeforeDetail, result.SkippedAlreadyProcessed,
                    result.SkippedMissingCallId, result.SkippedPreDetailOther, result.SkippedDueToQuotaExceeded,
                    result.DetailFetchFailed, result.Failed, result.ApiLimitExceeded,
                    result.WeeklyCallHistoryDetailRequests, result.WeeklyCallHistoryDetailLimit, result.WeeklyCdrListRequests);
            }

            return result;
        }

        // ─── Single-call helper for TestMode ───

        private async Task TryFetchAndProcessSingleAsync(
            string bearerToken, string callId, VoicenterRunResult result,
            bool forceRecheck, bool dryRun, CancellationToken ct)
        {
            try
            {
                var detailResult = await _api.FetchCallDetailAsync(bearerToken, callId, ct);
                result.CallHistoryDetailRequestsThisRun++;
                await _usage.RecordRequestAsync(
                    VoicenterEndpointType.CallHistoryDetail,
                    callId, detailResult.HttpStatus,
                    detailResult.Success, quotaExceeded: false,
                    errorMessage: detailResult.ErrorMessage, correlationId: null, ct);

                if (detailResult.Data == null)
                {
                    _logger.LogWarning("VOICENTER | TEST MODE | No detail returned for CallID={CallId}", callId);
                    return;
                }
                result.DetailsFetched = 1;
                await ProcessCallDetailAsync(detailResult.Data, result, dryRun, ct);
            }
            catch (VoicenterQuotaExceededException qx)
            {
                result.ApiLimitExceeded = true;
                await _usage.RecordRequestAsync(
                    VoicenterEndpointType.CallHistoryDetail,
                    callId, qx.HttpStatus, success: false, quotaExceeded: true,
                    errorMessage: qx.Message, correlationId: null, ct);
                _logger.LogError(qx, "VOICENTER | TEST MODE | Quota exceeded for CallID={CallId}", callId);
            }
        }

        // ─── Detail processing (post-fetch) ───

        private async Task ProcessCallDetailAsync(
            VoicenterCallDetail detail, VoicenterRunResult result, bool dryRun, CancellationToken ct)
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
                if (!dryRun)
                    await UpsertProcessingStateAsync(detail.CallId, VoicenterCallProcessingStatus.NoAi,
                        tikCounter: null, tikVisualId: null, lastError: null, ct);
                return;
            }

            var phone = ResolveCallPhone(detail);
            var normalizedPhone = NormalizeIsraeliPhone(phone);
            if (string.IsNullOrWhiteSpace(normalizedPhone))
            {
                result.SkippedNoMatch++;
                _logger.LogDebug("VOICENTER | Skip no normalizable phone | CallID={CallId}", detail.CallId);
                if (!dryRun)
                    await UpsertProcessingStateAsync(detail.CallId, VoicenterCallProcessingStatus.NoMatch,
                        tikCounter: null, tikVisualId: null, lastError: "No normalizable phone", ct);
                return;
            }

            var matches = await _phoneResolver.FindCasesByPhoneAsync(normalizedPhone, ct);
            if (matches.Count == 0)
            {
                result.SkippedNoMatch++;
                _logger.LogInformation(
                    "VOICENTER | Skip no case match after full Odcanit phone lookup | CallID={CallId}, Phone={Phone}, ResolverScope={ResolverScope}",
                    detail.CallId,
                    MaskPhone(normalizedPhone),
                    _phoneResolver.ScopeName);
                if (!dryRun)
                    await UpsertProcessingStateAsync(detail.CallId, VoicenterCallProcessingStatus.NoMatch,
                        tikCounter: null, tikVisualId: null, lastError: $"Phone matched no Odcanit cases; resolver scope={_phoneResolver.ScopeName}", ct);
                return;
            }

            _logger.LogInformation(
                "VOICENTER | Matched {Count} Odcanit case(s) | CallID={CallId}, Phone={Phone}, ResolverScope={ResolverScope}",
                matches.Count,
                detail.CallId,
                MaskPhone(normalizedPhone),
                _phoneResolver.ScopeName);
            foreach (var match in matches)
            {
                _logger.LogInformation(
                    "VOICENTER | Odcanit phone match | CallID={CallId}, TikNumber={TikNumber}, TikCounter={TikCounter}, MatchedField={MatchedField}",
                    detail.CallId,
                    match.TikNumber,
                    match.TikCounter,
                    match.MatchedField);
            }

            var annexText = BuildAnnexText(detail);
            var callIdHash = CallIdToSourceItemId(detail.CallId);

            int writtenForThisCall = 0;
            CasePhoneMatch? lastWritten = null;

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

                if (dryRun)
                {
                    _logger.LogInformation(
                        "VOICENTER BACKFILL DRYRUN | Would write annex | CallID={CallId}, TikNumber={TikNumber}, TikCounter={TikCounter}",
                        detail.CallId, match.TikNumber, match.TikCounter);
                    writtenForThisCall++;
                    lastWritten = match;
                    continue;
                }

                var ok = await WriteAnnexForCaseAsync(detail, match, annexText, callIdHash, result, ct);
                if (ok)
                {
                    writtenForThisCall++;
                    lastWritten = match;
                }
            }

            if (!dryRun)
            {
                if (writtenForThisCall > 0 && lastWritten != null)
                {
                    await UpsertProcessingStateAsync(detail.CallId, VoicenterCallProcessingStatus.Written,
                        tikCounter: lastWritten.TikCounter, tikVisualId: lastWritten.TikNumber, lastError: null, ct);
                }
                else
                {
                    // All matches were duplicates
                    await UpsertProcessingStateAsync(detail.CallId, VoicenterCallProcessingStatus.Duplicate,
                        tikCounter: null, tikVisualId: null, lastError: null, ct);
                }
            }
        }

        private async Task<bool> WriteAnnexForCaseAsync(
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
                return false;
            }

            _integrationDb.NispahWriteLogs.Add(writeLog);
            await _integrationDb.SaveChangesAsync(ct);
            result.Written++;
            return true;
        }

        // ─── Local-state helpers ───

        private bool ShouldSkipExistingState(VoicenterCallProcessingState state, bool forceRecheck, DateTime nowUtc)
        {
            if (!IsTerminalStatus(state.Status))
                return false;

            if (state.Status is VoicenterCallProcessingStatus.Written or VoicenterCallProcessingStatus.Duplicate)
                return true;

            if (forceRecheck)
                return false;

            if (state.Status == VoicenterCallProcessingStatus.QuotaExceeded)
            {
                var cutoff = nowUtc.AddDays(-Math.Max(0, _settings.ReprocessQuotaExceededLookbackDays));
                return _settings.ReprocessQuotaExceededLookbackDays <= 0 || state.FirstSeenUtc < cutoff;
            }

            if (state.Status == VoicenterCallProcessingStatus.NoMatch)
            {
                var cutoff = nowUtc.AddDays(-Math.Max(0, _settings.ReprocessNoMatchLookbackDays));
                return _settings.ReprocessNoMatchLookbackDays <= 0 || state.FirstSeenUtc < cutoff;
            }

            return true;
        }

        private async Task RetryDeferredStatesAsync(
            string bearerToken,
            VoicenterRunResult result,
            bool dryRun,
            HashSet<string> processedCallIds,
            CancellationToken ct)
        {
            var candidates = await LoadDeferredRetryCandidatesAsync(processedCallIds, ct);
            foreach (var state in candidates)
            {
                ct.ThrowIfCancellationRequested();

                if (await NispahLogsHaveSuccessForCallIdAsync(state.CallId, ct))
                {
                    result.SkippedDuplicateBeforeDetail++;
                    _logger.LogDebug(
                        "VOICENTER | Skip deferred retry duplicate before detail (NispahWriteLogs proof) | CallID={CallId}, State={State}",
                        state.CallId,
                        state.Status);
                    await UpsertProcessingStateAsync(state.CallId, VoicenterCallProcessingStatus.Written,
                        tikCounter: null, tikVisualId: null, lastError: null, ct);
                    continue;
                }

                VoicenterApiResult<VoicenterCallDetail?> detailResult;
                try
                {
                    _logger.LogInformation(
                        "VOICENTER | Deferred retry fetching CallHistoryDetail | CallID={CallId}, PriorState={State}",
                        state.CallId,
                        state.Status);
                    detailResult = await _api.FetchCallDetailAsync(bearerToken, state.CallId, ct);
                }
                catch (VoicenterQuotaExceededException qx)
                {
                    result.ApiLimitExceeded = true;
                    result.SkippedDueToQuotaExceeded++;
                    await _usage.RecordRequestAsync(
                        VoicenterEndpointType.CallHistoryDetail,
                        state.CallId, qx.HttpStatus, success: false, quotaExceeded: true,
                        errorMessage: qx.Message, correlationId: null, ct);
                    await UpsertProcessingStateAsync(state.CallId, VoicenterCallProcessingStatus.QuotaExceeded,
                        tikCounter: null, tikVisualId: null, lastError: qx.Message, ct);
                    _logger.LogWarning(
                        "VOICENTER | Quota exceeded during deferred retry; stopping deferred retries | CallID={CallId}",
                        state.CallId);
                    return;
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    result.DetailFetchFailed++;
                    result.Failed++;
                    if (result.FailedCallIds.Count < 20)
                        result.FailedCallIds.Add(state.CallId);
                    await _usage.RecordRequestAsync(
                        VoicenterEndpointType.CallHistoryDetail,
                        state.CallId, httpStatus: null, success: false, quotaExceeded: false,
                        errorMessage: ex.Message, correlationId: null, ct);
                    await UpsertProcessingStateAsync(state.CallId, VoicenterCallProcessingStatus.Failed,
                        tikCounter: null, tikVisualId: null, lastError: ex.Message, ct);
                    _logger.LogError(ex, "VOICENTER | Deferred detail fetch threw | CallID={CallId}", state.CallId);
                    continue;
                }

                result.CallHistoryDetailRequestsThisRun++;
                await _usage.RecordRequestAsync(
                    VoicenterEndpointType.CallHistoryDetail,
                    state.CallId,
                    detailResult.HttpStatus,
                    success: detailResult.Success,
                    quotaExceeded: false,
                    errorMessage: detailResult.ErrorMessage,
                    correlationId: null,
                    ct);

                if (!detailResult.Success || detailResult.Data is null)
                {
                    result.DetailFetchFailed++;
                    await UpsertProcessingStateAsync(state.CallId, VoicenterCallProcessingStatus.Failed,
                        tikCounter: null, tikVisualId: null,
                        lastError: detailResult.ErrorMessage ?? "Detail fetch returned no payload", ct);
                    continue;
                }

                result.DetailsFetched++;
                await ProcessCallDetailAsync(detailResult.Data, result, dryRun, ct);
            }
        }

        private async Task<List<VoicenterCallProcessingState>> LoadDeferredRetryCandidatesAsync(
            HashSet<string> processedCallIds,
            CancellationToken ct)
        {
            var nowUtc = DateTime.UtcNow;
            var query = _integrationDb.VoicenterCallProcessingStates.AsNoTracking();
            var candidates = new List<VoicenterCallProcessingState>();

            if (_settings.ReprocessQuotaExceededLookbackDays > 0)
            {
                var cutoff = nowUtc.AddDays(-_settings.ReprocessQuotaExceededLookbackDays);
                candidates.AddRange(await query
                    .Where(s => s.Status == VoicenterCallProcessingStatus.QuotaExceeded && s.FirstSeenUtc >= cutoff)
                    .OrderBy(s => s.FirstSeenUtc)
                    .ToListAsync(ct));
            }

            if (_settings.ReprocessNoMatchLookbackDays > 0)
            {
                var cutoff = nowUtc.AddDays(-_settings.ReprocessNoMatchLookbackDays);
                candidates.AddRange(await query
                    .Where(s => s.Status == VoicenterCallProcessingStatus.NoMatch && s.FirstSeenUtc >= cutoff)
                    .OrderBy(s => s.FirstSeenUtc)
                    .ToListAsync(ct));
            }

            return candidates
                .Where(s => !processedCallIds.Contains(s.CallId))
                .GroupBy(s => s.CallId, StringComparer.Ordinal)
                .Select(g => g.First())
                .Take(Math.Max(0, _settings.MaxDeferredReprocessCallsPerRun))
                .ToList();
        }

        private static bool IsTerminalStatus(string status) =>
            status is VoicenterCallProcessingStatus.Written
                  or VoicenterCallProcessingStatus.NoAi
                  or VoicenterCallProcessingStatus.NoMatch
                  or VoicenterCallProcessingStatus.Duplicate
                  or VoicenterCallProcessingStatus.QuotaExceeded;

        private async Task<bool> NispahLogsHaveSuccessForCallIdAsync(string callId, CancellationToken ct)
        {
            var callIdHash = CallIdToSourceItemId(callId);
            return await _integrationDb.NispahWriteLogs
                .AsNoTracking()
                .AnyAsync(w => w.SourceKind == SourceKind
                               && w.SourceItemId == callIdHash
                               && !w.Failed, ct);
        }

        private async Task UpsertProcessingStateAsync(
            string callId, string status, int? tikCounter, string? tikVisualId, string? lastError, CancellationToken ct)
        {
            try
            {
                var nowUtc = DateTime.UtcNow;
                var existing = await _integrationDb.VoicenterCallProcessingStates.FirstOrDefaultAsync(s => s.CallId == callId, ct);
                if (existing == null)
                {
                    _integrationDb.VoicenterCallProcessingStates.Add(new VoicenterCallProcessingState
                    {
                        CallId = callId,
                        FirstSeenUtc = nowUtc,
                        LastSeenUtc = nowUtc,
                        LastCheckedUtc = nowUtc,
                        Status = status,
                        Attempts = 1,
                        TikCounter = tikCounter,
                        TikVisualId = tikVisualId,
                        LastError = TrimMax(lastError, 2000),
                    });
                }
                else
                {
                    existing.LastSeenUtc = nowUtc;
                    existing.LastCheckedUtc = nowUtc;
                    existing.Status = status;
                    existing.Attempts += 1;
                    if (tikCounter.HasValue) existing.TikCounter = tikCounter;
                    if (!string.IsNullOrWhiteSpace(tikVisualId)) existing.TikVisualId = tikVisualId;
                    existing.LastError = TrimMax(lastError, 2000);
                }
                await _integrationDb.SaveChangesAsync(ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "VOICENTER | Failed to upsert VoicenterCallProcessingState | CallID={CallId}", callId);
            }
        }

        private async Task TouchLastSeenAsync(string callId, CancellationToken ct)
        {
            try
            {
                var existing = await _integrationDb.VoicenterCallProcessingStates.FirstOrDefaultAsync(s => s.CallId == callId, ct);
                if (existing != null)
                {
                    existing.LastSeenUtc = DateTime.UtcNow;
                    await _integrationDb.SaveChangesAsync(ct);
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "VOICENTER | TouchLastSeen failed | CallID={CallId}", callId);
            }
        }

        private async Task FinalizeWeeklyCountersAsync(VoicenterRunResult result, CancellationToken ct)
        {
            try
            {
                result.WeeklyCallHistoryDetailRequests =
                    await _usage.GetWeeklyCountAsync(VoicenterEndpointType.CallHistoryDetail, ct);
                result.WeeklyCdrListRequests =
                    await _usage.GetWeeklyCountAsync(VoicenterEndpointType.CdrList, ct);

                await _usage.MaybeSendWeeklyWarningAsync(
                    result.WeeklyCallHistoryDetailRequests,
                    result.WeeklyCdrListRequests,
                    quotaAlreadyExceeded: result.ApiLimitExceeded,
                    ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "VOICENTER | Failed to finalize weekly counters");
            }
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

        private static string? TrimMax(string? s, int max) =>
            string.IsNullOrEmpty(s) ? s : (s.Length <= max ? s : s[..max]);
    }
}
