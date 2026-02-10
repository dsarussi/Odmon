using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Globalization;
using Odmon.Worker.OdcanitAccess;
using Odmon.Worker.Data;
using Odmon.Worker.Monday;
using Odmon.Worker.Models;
using Odmon.Worker.Security;
using Odmon.Worker.Exceptions;
using Odmon.Worker.Configuration;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Odmon.Worker.Services
{
    public partial class SyncService
    {
        private const string DefaultClientPhoneColumnId = "phone_mkwe10tx";
        private const string DefaultClientEmailColumnId = "email_mkwefwgy";
        private static readonly TimeSpan ColumnCacheTtl = TimeSpan.FromMinutes(30);
        private readonly ICaseSource _caseSource;
        private readonly IntegrationDbContext _integrationDb;
        private readonly IMondayClient _mondayClient;
        private readonly IMondayMetadataProvider _mondayMetadataProvider;
        private readonly IConfiguration _config;
        private readonly ILogger<SyncService> _logger;
        private readonly ITestSafetyPolicy _safetyPolicy;
        private readonly MondaySettings _mondaySettings;
        private readonly ISecretProvider _secretProvider;
        private readonly HearingApprovalSyncService _hearingApprovalSyncService;
        private readonly HearingNearestSyncService _hearingNearestSyncService;
        private readonly ISkipLogger _skipLogger;
        private readonly IOdcanitReader _odcanitReader;
        private readonly IErrorNotifier _errorNotifier;
        private readonly OdcanitLoadOptions _odcanitLoadOptions;
        private readonly ConcurrentDictionary<long, ColumnCacheEntry> _columnIdCache = new();

        private const int MaxRetryAttempts = 3;
        private static readonly TimeSpan[] RetryDelays = { TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(12) };
        private static readonly TimeSpan RunLockDuration = TimeSpan.FromMinutes(30);

        public SyncService(
            ICaseSource caseSource,
            IntegrationDbContext integrationDb,
            IMondayClient mondayClient,
            IMondayMetadataProvider mondayMetadataProvider,
            IConfiguration config,
            ILogger<SyncService> logger,
            ITestSafetyPolicy safetyPolicy,
            IOptions<MondaySettings> mondayOptions,
            IOptions<OdcanitLoadOptions> odcanitLoadOptions,
            ISecretProvider secretProvider,
            HearingApprovalSyncService hearingApprovalSyncService,
            HearingNearestSyncService hearingNearestSyncService,
            ISkipLogger skipLogger,
            IOdcanitReader odcanitReader,
            IErrorNotifier errorNotifier)
        {
            _caseSource = caseSource;
            _integrationDb = integrationDb;
            _mondayClient = mondayClient;
            _mondayMetadataProvider = mondayMetadataProvider;
            _config = config;
            _logger = logger;
            _safetyPolicy = safetyPolicy;
            _mondaySettings = mondayOptions.Value ?? new MondaySettings();
            _odcanitLoadOptions = odcanitLoadOptions.Value ?? new OdcanitLoadOptions();
            _secretProvider = secretProvider;
            _hearingApprovalSyncService = hearingApprovalSyncService;
            _hearingNearestSyncService = hearingNearestSyncService;
            _skipLogger = skipLogger;
            _odcanitReader = odcanitReader;
            _errorNotifier = errorNotifier;
        }

        public async Task SyncOdcanitToMondayAsync(CancellationToken ct)
        {
            var runId = Guid.NewGuid().ToString("N");
            var runStartedAtUtc = DateTime.UtcNow;
            var runStopwatch = Stopwatch.StartNew();

            var enabled = _config.GetValue<bool>("Sync:Enabled", true);
            if (!enabled)
            {
                _logger.LogInformation("Sync is disabled via configuration.");
                return;
            }

            // ── Run-lock: prevent overlapping runs ──
            if (!await TryAcquireRunLockAsync(runId, ct))
            {
                _logger.LogWarning("Run-lock is held by another run. Skipping this cycle. RunId={RunId}", runId);
                return;
            }

            try
            {
            var dryRun = _config.GetValue<bool>("Sync:DryRun", false);
            var maxItems = _config.GetValue<int>("Sync:MaxItemsPerRun", 50);

            // Determine data source and derive testMode from actual source
            var testingEnabled = _config.GetValue<bool>("Testing:Enable", false);
            var odmonTestCasesEnabled = _config.GetValue<bool>("OdmonTestCases:Enable", false);
            
            string dataSource;
            bool testMode;
            
            if (odmonTestCasesEnabled)
            {
                dataSource = "OdmonTestCases";
                testMode = true;
            }
            else if (testingEnabled)
            {
                dataSource = "Testing (GuardOdcanitReader)";
                testMode = true;
            }
            else
            {
                dataSource = "Odcanit";
                testMode = false;
            }

            var safetySection = _config.GetSection("Safety");
            var testBoardId = safetySection.GetValue<long>("TestBoardId", 0);
            var testGroupId = _mondaySettings.TestGroupId;
            
            _logger.LogInformation(
                "Data source: {DataSource}, testMode={TestMode}",
                dataSource,
                testMode);

            var casesBoardId = _mondaySettings.CasesBoardId;
            var defaultGroupId = _mondaySettings.ToDoGroupId;
            if (string.IsNullOrWhiteSpace(defaultGroupId))
            {
                _logger.LogError("Monday ToDo group id is missing from configuration.");
                return;
            }

            var boardIdToUse = casesBoardId;
            var groupIdToUse = defaultGroupId;
            
            if (testMode)
            {
                if (testBoardId > 0)
                {
                    boardIdToUse = testBoardId;
                }

                if (!string.IsNullOrWhiteSpace(testGroupId))
                {
                    groupIdToUse = testGroupId;
                }
            }

            // Fail-fast: BoardId must never be 0 at runtime
            if (boardIdToUse == 0)
            {
                throw new InvalidOperationException("FATAL: boardIdToUse is 0. Check Monday:CasesBoardId and Safety:TestBoardId configuration. Aborting run.");
            }

            // ── Stage: Resolve TikCounters ──
            var stageTimer = Stopwatch.StartNew();
            int[] tikCounters = await DetermineTikCountersToLoadAsync(ct);
            stageTimer.Stop();
            _logger.LogInformation("Stage: ResolveTikCounters completed in {ElapsedMs}ms, count={Count}",
                stageTimer.ElapsedMilliseconds, tikCounters.Length);
            if (tikCounters.Length == 0)
            {
                _logger.LogError("No TikCounters to load. Worker stopped.");
                return;
            }

            // ── Stage: Load cases from Odcanit ──
            stageTimer.Restart();
            List<OdcanitCase> newOrUpdatedCases = await _odcanitReader.GetCasesByTikCountersAsync(tikCounters, ct);
            stageTimer.Stop();
            _logger.LogInformation("Stage: LoadCases completed in {ElapsedMs}ms, count={Count}",
                stageTimer.ElapsedMilliseconds, newOrUpdatedCases.Count);

            // ── Stage: Derive DocumentType ──
            stageTimer.Restart();
            foreach (var c in newOrUpdatedCases)
            {
                try
                {
                    c.DocumentType = DetermineDocumentTypeFromClientVisualId(c.ClientVisualID);
                    
                    _logger.LogDebug(
                        "DocumentType assigned: TikCounter={TikCounter}, TikNumber={TikNumber}, ClientVisualID='{ClientVisualID}', DocumentType='{DocumentType}'",
                        c.TikCounter,
                        c.TikNumber ?? "<null>",
                        c.ClientVisualID ?? "<null>",
                        c.DocumentType);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex,
                        "Failed to derive DocumentType for case TikCounter={TikCounter}, TikNumber={TikNumber}, ClientVisualID='{ClientVisualID}'. This case will fail validation. Exception: {Message}",
                        c.TikCounter,
                        c.TikNumber ?? "<null>",
                        c.ClientVisualID ?? "<null>",
                        ex.Message);
                }
            }
            stageTimer.Stop();
            _logger.LogInformation("Stage: DeriveDocumentType completed in {ElapsedMs}ms", stageTimer.ElapsedMilliseconds);

            // ── Stage: Per-case processing ──
            stageTimer.Restart();
            var batch = (maxItems > 0 ? newOrUpdatedCases.Take(maxItems) : newOrUpdatedCases).ToList();
            var processed = new List<object>();
            int created = 0, updated = 0;
            int skippedNonTest = 0, skippedExistingNonTest = 0, skippedNoChange = 0, skippedNonDemo = 0;
            int failed = 0;

            foreach (var c in batch)
            {
                var caseStopwatch = Stopwatch.StartNew();
                try
            {
                var caseBoardId = boardIdToUse;
                var caseGroupId = groupIdToUse;

                // In TikCounter-only mode, use test board/group if in test mode
                if (testMode)
                {
                    if (testBoardId > 0)
                    {
                        caseBoardId = testBoardId;
                    }

                    if (!string.IsNullOrWhiteSpace(testGroupId))
                    {
                        caseGroupId = testGroupId;
                    }
                }

                var (itemName, prefixApplied) = BuildItemName(c, testMode);
                string action = "unknown";
                bool wasNoChange = false;
                string? errorMessage = null;

                // Explicit override for the single test case (TikCounter == 31490)
                bool isExplicitTestCase = c.TikCounter == 31490;
                bool isSafeTestCase = _safetyPolicy.IsTestCase(c) || isExplicitTestCase;
                // In TikCounter-only mode, skip safety enforcement (all specified TikCounters should be processed)
                bool shouldEnforceSafety = false;

                // Find or create mapping for this case
                var mapping = await FindOrCreateMappingAsync(c, caseBoardId, itemName, testMode, dryRun, ct);
                long mondayIdForLog = mapping?.MondayItemId ?? 0;

                if (!testMode && shouldEnforceSafety)
                {
                    action = "skipped_non_demo";
                    skippedNonDemo++;

                    processed.Add(LogCase(action, c, itemName, prefixApplied, testMode, dryRun, caseBoardId, mondayIdForLog, wasNoChange, errorMessage));
                    continue;
                }

                if (testMode && shouldEnforceSafety)
                {
                    var hasMapping = mapping != null;
                    var mappingIsTest = mapping?.IsTest ?? false;

                    if (hasMapping && !mappingIsTest)
                    {
                        action = "skipped_non_test";
                        skippedNonTest++;
                        _logger.LogDebug(
                            "Skipping non-test case in TestMode: TikCounter={TikCounter}, TikNumber={TikNumber}, testMode={TestMode}, hasMapping={HasMapping}, mappingIsTest={MappingIsTest}",
                            c.TikCounter,
                            c.TikNumber,
                            testMode,
                            hasMapping,
                            mappingIsTest);

                        processed.Add(LogCase(action, c, itemName, prefixApplied, testMode, dryRun, caseBoardId, mondayIdForLog, wasNoChange, errorMessage));
                        continue;
                    }
                }

                if (mapping != null && testMode && !IsMappingTestCompatible(mapping))
                {
                    action = "skipped_existing_non_test_mapping";
                    skippedExistingNonTest++;

                    processed.Add(LogCase(action, c, itemName, prefixApplied, testMode, dryRun, caseBoardId, mondayIdForLog, wasNoChange, errorMessage));
                    continue;
                }

                // Determine whether to create, update, or skip
                var syncAction = DetermineSyncAction(mapping, c, caseBoardId, itemName);
                mondayIdForLog = mapping?.MondayItemId ?? mondayIdForLog;

                switch (syncAction.Action)
                {
                    case "create":
                        action = dryRun ? "dry-create" : "created";
                        _logger.LogInformation(
                            "Creating new Monday item: TikNumber={TikNumber}, TikCounter={TikCounter}, BoardId={BoardId}, ItemName={ItemName}",
                            c.TikNumber, c.TikCounter, caseBoardId, itemName);
                        if (!dryRun)
                        {
                            try
                            {
                                var (createResult, createRetries) = await ExecuteWithRetryAsync(
                                    () => CreateMondayItemAsync(c, caseBoardId, caseGroupId!, itemName, testMode, ct),
                                    "create_item", c.TikCounter, ct);
                                mondayIdForLog = createResult;
                                created++;
                                _logger.LogInformation(
                                    "Successfully created Monday item: TikNumber={TikNumber}, TikCounter={TikCounter}, MondayItemId={MondayItemId}, Retries={Retries}",
                                    c.TikNumber, c.TikCounter, mondayIdForLog, createRetries);
                            }
                            catch (CriticalFieldValidationException critEx)
                            {
                                action = "failed_create_validation";
                                failed++;
                                errorMessage = critEx.ValidationReason;
                                
                                _logger.LogError(
                                    "CRITICAL VALIDATION FAILED - Item NOT created: TikNumber={TikNumber}, TikCounter={TikCounter}, BoardId={BoardId}, ColumnId={ColumnId}, Value='{Value}', Reason={Reason}",
                                    c.TikNumber, c.TikCounter, caseBoardId, critEx.ColumnId, critEx.FieldValue ?? "<null>", critEx.ValidationReason);
                                
                                processed.Add(LogCase(action, c, itemName, prefixApplied, testMode, dryRun, caseBoardId, mondayIdForLog, wasNoChange, errorMessage));
                                continue;
                            }
                            catch (Exception ex) when (ex is not OperationCanceledException)
                            {
                                action = "failed_create";
                                failed++;
                                errorMessage = ex.Message;
                                
                                _logger.LogError(ex,
                                    "Error during create_item (after retries): TikNumber={TikNumber}, TikCounter={TikCounter}, BoardId={BoardId}, Error={Error}",
                                    c.TikNumber, c.TikCounter, caseBoardId, ex.Message);
                                
                                await PersistSyncFailureAsync(runId, c.TikCounter, c.TikNumber, caseBoardId, "create", ex, MaxRetryAttempts, ct);
                                processed.Add(LogCase(action, c, itemName, prefixApplied, testMode, dryRun, caseBoardId, mondayIdForLog, wasNoChange, errorMessage));
                                continue;
                            }
                        }
                        else
                        {
                            created++;
                        }
                        break;

                    case "update":
                        action = dryRun ? "dry-update" : "updated";
                        _logger.LogInformation(
                            "Updating existing Monday item: TikNumber={TikNumber}, TikCounter={TikCounter}, MondayItemId={MondayItemId}, BoardId={BoardId}, RequiresDataUpdate={RequiresDataUpdate}, RequiresNameUpdate={RequiresNameUpdate}, RequiresHearingUpdate={RequiresHearingUpdate}",
                            c.TikNumber, c.TikCounter, mapping!.MondayItemId, caseBoardId, syncAction.RequiresDataUpdate, syncAction.RequiresNameUpdate, syncAction.RequiresHearingUpdate);
                        if (!dryRun)
                        {
                            try
                            {
                                var itemState = await _mondayClient.GetItemStateAsync(caseBoardId, mapping.MondayItemId, ct);
                                if (itemState != null && !string.Equals(itemState, "active", StringComparison.OrdinalIgnoreCase))
                                {
                                    var oldItemId = mapping.MondayItemId;
                                    var columnValuesJson = await BuildColumnValuesJsonAsync(caseBoardId, c, forceNotStartedStatus: true, ct);
                                    var (recreateResult, _) = await ExecuteWithRetryAsync(
                                        () => _mondayClient.CreateItemAsync(caseBoardId, caseGroupId!, itemName, columnValuesJson, ct),
                                        "recreate_inactive_item", c.TikCounter, ct);
                                    mapping.MondayItemId = recreateResult;
                                    mapping.OdcanitVersion = c.tsModifyDate?.ToString("o") ?? string.Empty;
                                    mapping.MondayChecksum = itemName;
                                    mapping.HearingChecksum = ComputeHearingChecksum(c);
                                    mapping.LastSyncFromOdcanitUtc = DateTime.UtcNow;
                                    mapping.IsTest = testMode;
                                    await _integrationDb.SaveChangesAsync(ct);
                                    _logger.LogWarning(
                                        "Monday item inactive (state={State}), created new item and updated mapping: TikCounter={TikCounter}, TikNumber={TikNumber}, oldItemId={OldItemId}, newItemId={NewItemId}",
                                        itemState, c.TikCounter, c.TikNumber ?? "<null>", oldItemId, recreateResult);
                                    updated++;
                                }
                                else
                                {
                                    var (__, updateRetries) = await ExecuteWithRetryAsync(async () =>
                                    {
                                        await UpdateMondayItemAsync(mapping!, c, caseBoardId, itemName, syncAction.RequiresNameUpdate, syncAction.RequiresDataUpdate, syncAction.RequiresHearingUpdate, testMode, ct);
                                        return true;
                                    }, "update_item", c.TikCounter, ct);
                                updated++;
                                _logger.LogInformation(
                                        "Successfully updated Monday item: TikNumber={TikNumber}, TikCounter={TikCounter}, MondayItemId={MondayItemId}, Retries={Retries}",
                                        c.TikNumber, c.TikCounter, mapping.MondayItemId, updateRetries);
                            }
                            }
                            catch (CriticalFieldValidationException critEx)
                            {
                                action = "failed_update_validation";
                                failed++;
                                errorMessage = critEx.ValidationReason;
                                
                                _logger.LogError(
                                    "CRITICAL VALIDATION FAILED - Item NOT updated: TikNumber={TikNumber}, TikCounter={TikCounter}, MondayItemId={MondayItemId}, BoardId={BoardId}, ColumnId={ColumnId}, Value='{Value}', Reason={Reason}",
                                    c.TikNumber, c.TikCounter, mapping.MondayItemId, caseBoardId, critEx.ColumnId, critEx.FieldValue ?? "<null>", critEx.ValidationReason);
                                
                                processed.Add(LogCase(action, c, itemName, prefixApplied, testMode, dryRun, caseBoardId, mondayIdForLog, wasNoChange, errorMessage));
                                continue;
                            }
                            catch (Exception ex) when (ex is not OperationCanceledException)
                            {
                                action = "failed_update";
                                failed++;
                                errorMessage = ex.Message;
                                
                                _logger.LogError(ex,
                                    "Error during update (after retries): TikNumber={TikNumber}, TikCounter={TikCounter}, MondayItemId={MondayItemId}, BoardId={BoardId}, Error={Error}",
                                    c.TikNumber, c.TikCounter, mapping.MondayItemId, caseBoardId, ex.Message);
                                
                                await PersistSyncFailureAsync(runId, c.TikCounter, c.TikNumber, caseBoardId, "update", ex, MaxRetryAttempts, ct);
                                processed.Add(LogCase(action, c, itemName, prefixApplied, testMode, dryRun, caseBoardId, mondayIdForLog, wasNoChange, errorMessage));
                                continue;
                            }
                        }
                        else
                        {
                            updated++;
                        }
                        break;

                    case "skip":
                        action = "skipped_no_change";
                        wasNoChange = true;
                        skippedNoChange++;
                        _logger.LogDebug(
                            "Skipping item (no changes): TikNumber={TikNumber}, TikCounter={TikCounter}, MondayItemId={MondayItemId}",
                            c.TikNumber, c.TikCounter, mapping?.MondayItemId ?? 0);
                        break;
                }

                processed.Add(LogCase(action, c, itemName, prefixApplied, testMode, dryRun, caseBoardId, mondayIdForLog, wasNoChange, errorMessage));

                } // end try (per-case)
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    failed++;
                    _logger.LogError(ex,
                        "UNHANDLED error processing case TikCounter={TikCounter}, TikNumber={TikNumber}, BoardId={BoardId}: {Error}",
                        c.TikCounter, c.TikNumber ?? "<null>", boardIdToUse, ex.Message);
                    await PersistSyncFailureAsync(runId, c.TikCounter, c.TikNumber, boardIdToUse, "process_case", ex, 0, ct);
                    processed.Add(LogCase("failed_unhandled", c, c.TikName ?? c.TikNumber ?? "<unknown>", false, testMode, dryRun, boardIdToUse, 0, false, ex.Message));
                }
                finally
                {
                    caseStopwatch.Stop();
                    _logger.LogDebug("Case TikCounter={TikCounter} processed in {ElapsedMs}ms", c.TikCounter, caseStopwatch.ElapsedMilliseconds);
                }
            }

            stageTimer.Stop();
            _logger.LogInformation("Stage: PerCaseProcessing completed in {ElapsedMs}ms for {BatchCount} cases", stageTimer.ElapsedMilliseconds, batch.Count);

            // ── Run summary ──
            runStopwatch.Stop();
            var totalDurationMs = runStopwatch.ElapsedMilliseconds;
            var successCount = created + updated + skippedNoChange;

            var runSummary = new
            {
                RunId = runId,
                StartedAtUtc = runStartedAtUtc,
                FinishedAtUtc = DateTime.UtcNow,
                DurationMs = totalDurationMs,
                MaxItems = maxItems,
                Total = batch.Count,
                Success = successCount,
                Created = created,
                Updated = updated,
                SkippedNonTest = skippedNonTest,
                SkippedExistingNonTestMapping = skippedExistingNonTest,
                SkippedNonDemo = skippedNonDemo,
                SkippedNoChange = skippedNoChange,
                Failed = failed,
                Processed = processed
            };

            _integrationDb.SyncLogs.Add(new SyncLog
            {
                CreatedAtUtc = DateTime.UtcNow,
                Source = "SyncService",
                Level = failed > 0 ? "Warning" : "Info",
                Message = $"Run {runId}: total={batch.Count}, success={successCount}, created={created}, updated={updated}, skipped={skippedNoChange}, failed={failed}, duration={totalDurationMs}ms",
                Details = JsonSerializer.Serialize(runSummary)
            });

            await _integrationDb.SaveChangesAsync(ct);

            // Phase-2: hearing approval write-back runs even when main sync skips/no-change
            stageTimer.Restart();
            await _hearingApprovalSyncService.SyncAsync(batch, ct);
            stageTimer.Stop();
            _logger.LogInformation("Stage: HearingApprovalSync completed in {ElapsedMs}ms", stageTimer.ElapsedMilliseconds);

            // Nearest hearing sync: update Monday hearing date/judge/city/status from vwExportToOuterSystems_YomanData
            stageTimer.Restart();
            await _hearingNearestSyncService.SyncNearestHearingsAsync(boardIdToUse, ct);
            stageTimer.Stop();
            _logger.LogInformation("Stage: HearingNearestSync completed in {ElapsedMs}ms", stageTimer.ElapsedMilliseconds);

            // ── SYNC RUN SUMMARY ──
            _logger.LogInformation(
                "SYNC RUN SUMMARY | RunId={RunId} | Total={Total} | Success={Success} | Created={Created} | Updated={Updated} | " +
                "SkippedNoChange={SkippedNoChange} | Failed={Failed} | Duration={DurationMs}ms",
                runId, batch.Count, successCount, created, updated, skippedNoChange, failed, totalDurationMs);

            // ── Failure rate notification ──
            if (batch.Count > 0 && failed > 0)
            {
                var failureRate = (double)failed / batch.Count;
                if (failureRate >= 0.5)
                {
                    try { await _errorNotifier.NotifyHighFailureRateAsync(runId, batch.Count, failed, ct); }
                    catch (Exception nex) { _logger.LogWarning(nex, "Error notifier failed"); }
                }
                else
            {
                _logger.LogWarning(
                        "Sync run {RunId} completed with {FailedCount} failure(s). Failed cases are persisted in SyncFailures table.",
                    runId, failed);
                }
            }

            // ── HEARTBEAT ──
            _logger.LogInformation(
                "HEARTBEAT | ODMON sync run {RunId} completed at {FinishedAtUtc:O} | Total={Total}, Failed={Failed}, Duration={DurationMs}ms",
                runId, DateTime.UtcNow, batch.Count, failed, totalDurationMs);

            } // end try (run-level)
            finally
            {
                await ReleaseRunLockAsync(runId, ct);
            }
        }

        private object LogCase(
            string action,
            OdcanitCase c,
            string itemName,
            bool prefixApplied,
            bool testMode,
            bool dryRun,
            long boardIdUsed,
            long mondayItemId,
            bool wasNoChange,
            string? errorMessage)
        {
            var details = new
            {
                c.TikCounter,
                c.TikNumber,
                c.tsModifyDate,
                Action = action,
                ItemName = itemName,
                IsDryRun = dryRun,
                IsTestMode = testMode,
                BoardIdUsed = boardIdUsed,
                MondayItemId = mondayItemId,
                PrefixApplied = prefixApplied,
                WasNoChange = wasNoChange,
                Error = errorMessage
            };

            var message = $"{action} TikCounter={c.TikCounter}, TikNumber={c.TikNumber}, testMode={testMode}, boardId={boardIdUsed}";
            if (!string.IsNullOrWhiteSpace(errorMessage))
            {
                message += $", error={errorMessage}";
            }

            _integrationDb.SyncLogs.Add(new SyncLog
            {
                CreatedAtUtc = DateTime.UtcNow,
                Source = "SyncService",
                Level = string.IsNullOrWhiteSpace(errorMessage) ? "Info" : "Error",
                Message = message,
                Details = JsonSerializer.Serialize(details)
            });

            if (string.IsNullOrWhiteSpace(errorMessage))
            {
                _logger.LogInformation(
                    "Case {TikCounter} ({TikNumber}) action={Action} testMode={TestMode} boardId={BoardId} dryRun={DryRun} prefixApplied={PrefixApplied}",
                    c.TikCounter,
                    c.TikNumber,
                    action,
                    testMode,
                    boardIdUsed,
                    dryRun,
                    prefixApplied);
            }
            else
            {
                _logger.LogError(
                    "Case {TikCounter} ({TikNumber}) action={Action} testMode={TestMode} boardId={BoardId} dryRun={DryRun} prefixApplied={PrefixApplied} error={Error}",
                    c.TikCounter,
                    c.TikNumber,
                    action,
                    testMode,
                    boardIdUsed,
                    dryRun,
                    prefixApplied,
                    errorMessage);
            }

            return details;
        }

        private static (string ItemName, bool PrefixApplied) BuildItemName(OdcanitCase c, bool testMode)
        {
            var baseName = BuildBaseItemName(c);
            var prefixApplied = false;

            if (testMode && !baseName.StartsWith("[TEST] ", StringComparison.Ordinal))
            {
                baseName = $"[TEST] {baseName}";
                prefixApplied = true;
            }

            return (baseName, prefixApplied);
        }

        private static string BuildBaseItemName(OdcanitCase c)
        {
            var tikNumber = (c.TikNumber ?? string.Empty).Trim();
            var clientName = (c.ClientName ?? string.Empty).Trim();
            var tikName = (c.TikName ?? string.Empty).Trim();
            var policyHolderName = (c.PolicyHolderName ?? string.Empty).Trim();
            var referenceNumber = !string.IsNullOrEmpty(tikNumber)
                ? tikNumber
                : c.TikCounter.ToString(CultureInfo.InvariantCulture);

            if (!string.IsNullOrEmpty(clientName) && !string.IsNullOrEmpty(policyHolderName))
            {
                return $"{clientName} - שם בעל פוליסה: {policyHolderName} ({referenceNumber})";
            }

            if (!string.IsNullOrEmpty(clientName))
            {
                return $"{clientName} ({referenceNumber})";
            }

            if (!string.IsNullOrEmpty(policyHolderName))
            {
                return $"{policyHolderName} ({referenceNumber})";
            }

            if (!string.IsNullOrEmpty(tikName))
            {
                return $"{tikName} ({referenceNumber})";
            }

            return referenceNumber;
        }

        private async Task<string> BuildColumnValuesJsonAsync(long boardId, OdcanitCase c, bool forceNotStartedStatus = false, CancellationToken ct = default)
        {
            var columnValues = new Dictionary<string, object>();

            TryAddStringColumn(columnValues, _mondaySettings.CaseNumberColumnId, c.TikNumber);
            await TryAddClientNumberDropdownAsync(columnValues, boardId, _mondaySettings.ClientNumberColumnId, c.ClientVisualID, c.TikCounter, c.TikNumber, ct);
            TryAddStringColumn(columnValues, _mondaySettings.ClaimNumberColumnId, c.Additional ?? c.HozlapTikNumber);

            var normalizedPolicyHolderPhone = string.IsNullOrWhiteSpace(c.PolicyHolderPhone)
                ? null
                : c.PolicyHolderPhone.Trim();

            var mainPhoneColumnId = ResolveClientPhoneColumnId();
            TryAddPhoneColumn(columnValues, mainPhoneColumnId, normalizedPolicyHolderPhone, c.TikCounter, "Policy holder phone (טלפון column)");
            TryAddStringColumn(columnValues, ResolveClientEmailColumnId(), c.ClientEmail);

            TryAddDateColumn(columnValues, _mondaySettings.CaseOpenDateColumnId, c.tsCreateDate);
            TryAddDateColumn(columnValues, _mondaySettings.EventDateColumnId, c.EventDate);
            TryAddDateColumn(columnValues, _mondaySettings.CaseCloseDateColumnId, c.TikCloseDate);
            TryAddDateColumn(columnValues, _mondaySettings.ComplaintReceivedDateColumnId, c.ComplaintReceivedDate);

            // ── Hearing fields derived exclusively from the selected diary event row ──
            // EffectiveCourtCity: HearingCity (from diary) -> HearingCourtName fallback -> null
            var effectiveCourtCity = !string.IsNullOrWhiteSpace(c.HearingCity) ? c.HearingCity.Trim()
                : (!string.IsNullOrWhiteSpace(c.HearingCourtName) ? c.HearingCourtName.Trim() : null);

            var hasHearingDate = c.HearingDate.HasValue;
            var hasHearingJudge = !string.IsNullOrWhiteSpace(c.HearingJudgeName);
            var hasEffectiveCourtCity = !string.IsNullOrWhiteSpace(effectiveCourtCity);
            var meetStatus = c.MeetStatus;

            // Date/hour gating:
            //   Require BOTH HearingJudgeName AND EffectiveCourtCity (trigger safety).
            //   Block if MeetStatus==1 (canceled hearing — date is stale).
            //   Allow MeetStatus==0 (active) and MeetStatus==2 (transferred — new date is relevant).
            var canPublishDateHour = hasHearingDate
                && hasHearingJudge
                && hasEffectiveCourtCity
                && meetStatus != 1;

            _logger.LogDebug(
                "Hearing gating TikCounter={TikCounter}, TikNumber={TikNumber}: Date={HasDate}, HearingJudge={HasJudge}, " +
                "CitySource={CitySource}->EffectiveCourtCity='{EffectiveCourtCity}', MeetStatus={MeetStatus}, CanPublishDateHour={CanPublishDateHour}",
                c.TikCounter,
                c.TikNumber ?? "<null>",
                hasHearingDate,
                hasHearingJudge,
                !string.IsNullOrWhiteSpace(c.HearingCity) ? "HearingCity" : (!string.IsNullOrWhiteSpace(c.HearingCourtName) ? "HearingCourtName" : "none"),
                effectiveCourtCity ?? "<null>",
                meetStatus?.ToString() ?? "<null>",
                canPublishDateHour);

            // Hearing columns included in this payload (for logging)
            var hearingColumnsIncluded = new List<string>();

            // Date/hour: when gating passes
            if (canPublishDateHour)
            {
            TryAddDateColumn(columnValues, _mondaySettings.HearingDateColumnId, c.HearingDate);
                await TryAddHourColumnAsync(columnValues, boardId, _mondaySettings.HearingHourColumnId, c.HearingTime, c.TikCounter, ct);
                if (!string.IsNullOrWhiteSpace(_mondaySettings.HearingDateColumnId)) hearingColumnsIncluded.Add(_mondaySettings.HearingDateColumnId);
                if (!string.IsNullOrWhiteSpace(_mondaySettings.HearingHourColumnId) && c.HearingTime.HasValue) hearingColumnsIncluded.Add(_mondaySettings.HearingHourColumnId);
            }
            else if (hasHearingDate && !canPublishDateHour)
            {
                _logger.LogInformation(
                    "Hearing date/hour update blocked for TikCounter={TikCounter}: HasHearingJudge={HasJudge}, HasEffectiveCourtCity={HasCity}, MeetStatus={MeetStatus}",
                    c.TikCounter, hasHearingJudge, hasEffectiveCourtCity, meetStatus?.ToString() ?? "<null>");
            }

            // Judge: update independently from diary event if exists
            if (hasHearingJudge)
            {
                TryAddStringColumn(columnValues, _mondaySettings.JudgeNameColumnId, c.HearingJudgeName!.Trim());
                if (!string.IsNullOrWhiteSpace(_mondaySettings.JudgeNameColumnId)) hearingColumnsIncluded.Add(_mondaySettings.JudgeNameColumnId);
            }

            // Court city: use EffectiveCourtCity from diary event, update independently
            if (hasEffectiveCourtCity)
            {
                TryAddStringColumn(columnValues, _mondaySettings.CourtCityColumnId, effectiveCourtCity);
                if (!string.IsNullOrWhiteSpace(_mondaySettings.CourtCityColumnId)) hearingColumnsIncluded.Add(_mondaySettings.CourtCityColumnId);
            }

            // Hearing status column "דיון התבטל?" - independent of date/hour gating
            if (meetStatus == 1 && !string.IsNullOrWhiteSpace(_mondaySettings.HearingStatusColumnId))
            {
                columnValues[_mondaySettings.HearingStatusColumnId] = new { label = "מבוטל" };
                hearingColumnsIncluded.Add(_mondaySettings.HearingStatusColumnId);
                _logger.LogDebug(
                    "Hearing status set to 'מבוטל' for TikCounter={TikCounter}, MeetStatus={MeetStatus}, ColumnId={ColumnId}",
                    c.TikCounter, meetStatus, _mondaySettings.HearingStatusColumnId);
            }
            else if (meetStatus == 2 && !string.IsNullOrWhiteSpace(_mondaySettings.HearingStatusColumnId))
            {
                columnValues[_mondaySettings.HearingStatusColumnId] = new { label = "הועבר" };
                hearingColumnsIncluded.Add(_mondaySettings.HearingStatusColumnId);
                _logger.LogDebug(
                    "Hearing status set to 'הועבר' for TikCounter={TikCounter}, MeetStatus={MeetStatus}, ColumnId={ColumnId}",
                    c.TikCounter, meetStatus, _mondaySettings.HearingStatusColumnId);
            }
            else if (meetStatus == 0 || meetStatus == null)
            {
                // MeetStatus 0 (active) or null (no hearing): OMIT status column to preserve manual values
                _logger.LogDebug(
                    "Hearing status omitted (active/none) for TikCounter={TikCounter}, MeetStatus={MeetStatus}",
                    c.TikCounter, meetStatus?.ToString() ?? "<null>");
            }

            if (hearingColumnsIncluded.Count > 0)
            {
                _logger.LogDebug(
                    "Hearing columns included in payload for TikCounter={TikCounter}: [{ColumnIds}]",
                    c.TikCounter, string.Join(", ", hearingColumnsIncluded));
            }

            TryAddDecimalColumn(columnValues, _mondaySettings.RequestedClaimAmountColumnId, c.RequestedClaimAmount);
            TryAddDecimalColumn(columnValues, _mondaySettings.ProvenClaimAmountColumnId, c.ProvenClaimAmount);
            TryAddDecimalColumn(columnValues, _mondaySettings.JudgmentAmountColumnId, c.JudgmentAmount);
            TryAddDecimalColumn(columnValues, _mondaySettings.AppraiserFeeAmountColumnId, c.AppraiserFeeAmount);
            TryAddDecimalColumn(columnValues, _mondaySettings.DirectDamageAmountColumnId, c.DirectDamageAmount);
            TryAddDecimalColumn(columnValues, _mondaySettings.OtherLossesAmountColumnId, c.OtherLossesAmount);
            TryAddDecimalColumn(columnValues, _mondaySettings.LossOfValueAmountColumnId, c.LossOfValueAmount);
            TryAddDecimalColumn(columnValues, _mondaySettings.ResidualValueAmountColumnId, c.ResidualValueAmount);
            TryAddLongTextColumn(columnValues, _mondaySettings.NotesColumnId, c.Notes);
            TryAddStringColumn(columnValues, _mondaySettings.ClientAddressColumnId, c.ClientAddress);
            TryAddStringColumn(columnValues, _mondaySettings.ClientTaxIdColumnId, c.ClientTaxId);
            
            // Use PolicyHolderNameColumnId from settings
            TryAddStringColumn(columnValues, _mondaySettings.PolicyHolderNameColumnId, c.PolicyHolderName);
            TryAddStringColumn(columnValues, _mondaySettings.PolicyHolderIdColumnId, c.PolicyHolderId);
            TryAddStringColumn(columnValues, _mondaySettings.PolicyHolderAddressColumnId, c.PolicyHolderAddress);
            TryAddPhoneColumn(columnValues, _mondaySettings.PolicyHolderPhoneColumnId, normalizedPolicyHolderPhone, c.TikCounter, "Policy holder phone");
            TryAddStringColumn(columnValues, _mondaySettings.PolicyHolderEmailColumnId, c.PolicyHolderEmail);
            TryAddStringColumn(columnValues, _mondaySettings.MainCarNumberColumnId, c.MainCarNumber);
            TryAddStringColumn(columnValues, _mondaySettings.DriverNameColumnId, c.DriverName);
            TryAddStringColumn(columnValues, _mondaySettings.DriverIdColumnId, c.DriverId);
            TryAddPhoneColumn(columnValues, _mondaySettings.DriverPhoneColumnId, c.DriverPhone, c.TikCounter, "Driver phone");
            TryAddStringColumn(columnValues, _mondaySettings.WitnessNameColumnId, c.WitnessName);
            TryAddStringColumn(columnValues, _mondaySettings.AdditionalDefendantsColumnId, c.AdditionalDefendants);
            TryAddStringColumn(columnValues, _mondaySettings.PlaintiffNameColumnId, c.PlaintiffName);
            TryAddStringColumn(columnValues, _mondaySettings.PlaintiffIdColumnId, c.PlaintiffId);
            TryAddStringColumn(columnValues, _mondaySettings.PlaintiffAddressColumnId, c.PlaintiffAddress);
            TryAddPhoneColumn(columnValues, _mondaySettings.PlaintiffPhoneColumnId, c.PlaintiffPhone, c.TikCounter, "Plaintiff phone");
            TryAddStringColumn(columnValues, _mondaySettings.PlaintiffEmailColumnId, c.PlaintiffEmail);
            TryAddStringColumn(columnValues, _mondaySettings.DefendantNameColumnId, c.DefendantName);
            TryAddStringColumn(columnValues, _mondaySettings.DefendantFaxColumnId, c.DefendantFax);
            TryAddStringColumn(columnValues, _mondaySettings.ThirdPartyDriverNameColumnId, c.ThirdPartyDriverName);
            TryAddStringColumn(columnValues, _mondaySettings.ThirdPartyDriverIdColumnId, c.ThirdPartyDriverId);
            TryAddStringColumn(columnValues, _mondaySettings.ThirdPartyCarNumberColumnId, c.ThirdPartyCarNumber);
            TryAddPhoneColumn(columnValues, _mondaySettings.ThirdPartyPhoneColumnId, c.ThirdPartyPhone, c.TikCounter, "Third-party phone");
            // TODO: Verify that insurer names exist as labels on the Monday board before relying on this mapping.
            TryAddStatusLabelColumn(columnValues, _mondaySettings.ThirdPartyInsurerStatusColumnId, c.ThirdPartyInsurerName);
            TryAddStringColumn(columnValues, _mondaySettings.InsuranceCompanyIdColumnId, c.InsuranceCompanyId);
            TryAddStringColumn(columnValues, _mondaySettings.InsuranceCompanyAddressColumnId, c.InsuranceCompanyAddress);
            TryAddStringColumn(columnValues, _mondaySettings.InsuranceCompanyEmailColumnId, c.InsuranceCompanyEmail);
            TryAddStringColumn(columnValues, _mondaySettings.ThirdPartyEmployerNameColumnId, c.ThirdPartyEmployerName);
            TryAddStringColumn(columnValues, _mondaySettings.ThirdPartyEmployerIdColumnId, c.ThirdPartyEmployerId);
            TryAddStringColumn(columnValues, _mondaySettings.ThirdPartyEmployerAddressColumnId, c.ThirdPartyEmployerAddress);
            TryAddStringColumn(columnValues, _mondaySettings.ThirdPartyLawyerNameColumnId, c.ThirdPartyLawyerName);
            TryAddStringColumn(columnValues, _mondaySettings.ThirdPartyLawyerAddressColumnId, c.ThirdPartyLawyerAddress);
            TryAddPhoneColumn(columnValues, _mondaySettings.ThirdPartyLawyerPhoneColumnId, c.ThirdPartyLawyerPhone, c.TikCounter, "Third-party lawyer phone");
            TryAddStringColumn(columnValues, _mondaySettings.ThirdPartyLawyerEmailColumnId, c.ThirdPartyLawyerEmail);
            // TODO: Ensure court labels on Monday match Odcanit court names.
            TryAddStatusLabelColumn(columnValues, _mondaySettings.CourtNameStatusColumnId, c.CourtName);
            // CourtCity and JudgeName are handled above in the hearing gating section with EffectiveCourtCity logic
            TryAddStringColumn(columnValues, _mondaySettings.CourtCaseNumberColumnId, c.CourtCaseNumber);
            TryAddStringColumn(columnValues, _mondaySettings.AttorneyNameColumnId, c.AttorneyName);
            TryAddStringColumn(columnValues, _mondaySettings.DefenseStreetColumnId, c.DefenseStreet);
            TryAddStringColumn(columnValues, _mondaySettings.ClaimStreetColumnId, c.ClaimStreet);
            TryAddStringColumn(columnValues, _mondaySettings.CaseFolderIdColumnId, c.CaseFolderId);
            TryAddStatusLabelColumn(columnValues, _mondaySettings.TaskTypeStatusColumnId, MapTaskTypeLabel(c.TikType));

            // Legal user data (UserData view vwExportToOuterSystems_UserData): צד תובע / צד נתבע -> Monday status columns
            TryAddStatusLabelColumn(columnValues, "color_mkxh8gsq", MapPlaintiffSideLabel(c.PlaintiffSideRaw));
            TryAddStatusLabelColumn(columnValues, "color_mkxh5x31", MapDefendantSideLabel(c.DefendantSideRaw));
            TryAddStringColumn(columnValues, _mondaySettings.ResponsibleTextColumnId, DetermineResponsibleText(c));

            // DocumentType was already derived and assigned immediately after loading the case
            // (see line ~157 in RunAsync - authoritative assignment point)
            // Here we just use the already-assigned value
            
            // Special handling for Client 6: Do NOT send DocumentType to Monday
            // Client 6 uses "מכתב דרישה אילי" which doesn't exist in Monday labels
            var isClient6 = IsClient6(c.ClientVisualID);
            
            if (!isClient6)
            {
                var documentType = c.DocumentType;
            if (!string.IsNullOrWhiteSpace(documentType))
            {
                TryAddStatusLabelColumn(columnValues, _mondaySettings.DocumentTypeStatusColumnId, documentType);
                }
            }
            else
            {
                _logger.LogDebug(
                    "Client 6 special handling: Omitting DocumentType for TikCounter={TikCounter}, TikNumber={TikNumber}, ClientVisualID='{ClientVisualID}'",
                    c.TikCounter,
                    c.TikNumber ?? "<null>",
                    c.ClientVisualID ?? "<null>");
            }

            var statusColumnId = _mondaySettings.CaseStatusColumnId;
            if (!string.IsNullOrWhiteSpace(statusColumnId))
            {
                if (forceNotStartedStatus)
                {
                    columnValues[statusColumnId] = new { label = "חדש" };
                }
                else
                {
                    var statusIndex = MapStatusIndex(c.StatusName);
                    columnValues[statusColumnId] = new { index = statusIndex };
                }
            }

            if (columnValues.TryGetValue(mainPhoneColumnId, out var phoneColumnValue))
            {
                _logger.LogDebug("Policy holder phone payload for TikCounter {TikCounter}: column {ColumnId} value={Value}", c.TikCounter, mainPhoneColumnId, phoneColumnValue ?? "<null>");
            }
            else
            {
                _logger.LogDebug("Policy holder phone payload for TikCounter {TikCounter}: column {ColumnId} left empty", c.TikCounter, mainPhoneColumnId);
            }

            // Validate hour column values before serialization
            ValidateHourColumnValues(columnValues, c.TikCounter);

            // DEBUG: Log column values before filtering
            _logger.LogInformation(
                "BuildColumnValues BEFORE filter: TikCounter={TikCounter}, TikNumber={TikNumber}, BoardId={BoardId}, Count={Count}, ColumnIds={ColumnIds}",
                c.TikCounter,
                c.TikNumber ?? "<null>",
                boardId,
                columnValues.Count,
                string.Join(", ", columnValues.Keys));

            // Filter out invalid column IDs before serialization
            await FilterInvalidColumnsAsync(boardId, columnValues, c, ct);

            // Address and notes mapping diagnostics
            var defenseColumnId = _mondaySettings.DefenseStreetColumnId;
            var notesColumnId = _mondaySettings.NotesColumnId;

            var hasDefense = !string.IsNullOrWhiteSpace(defenseColumnId) && columnValues.ContainsKey(defenseColumnId);
            var hasNotes = !string.IsNullOrWhiteSpace(notesColumnId) && columnValues.ContainsKey(notesColumnId);

            _logger.LogDebug(
                "Address mapping TikCounter {TikCounter}: ColumnId={ColumnId}, Value='{Value}', Included={Included}",
                c.TikCounter,
                defenseColumnId ?? "<null>",
                hasDefense ? (columnValues[defenseColumnId!] ?? "<null>") : "<not included>",
                hasDefense);

            _logger.LogDebug(
                "Notes mapping TikCounter {TikCounter}: ColumnId={ColumnId}, Len={Len}, Included={Included}",
                c.TikCounter,
                notesColumnId ?? "<null>",
                c.Notes?.Length ?? 0,
                hasNotes);

            if (!string.IsNullOrWhiteSpace(c.DefenseStreet) && !hasDefense)
            {
                _logger.LogWarning(
                    "Expected Monday column missing from payload: {ColumnId} (value present) TikCounter {TikCounter}",
                    defenseColumnId ?? "<null>",
                    c.TikCounter);
            }

            if (!string.IsNullOrWhiteSpace(c.Notes) && !hasNotes)
            {
                _logger.LogWarning(
                    "Expected Monday column missing from payload: {ColumnId} (value present) TikCounter {TikCounter}",
                    notesColumnId ?? "<null>",
                    c.TikCounter);
            }

            // DEBUG: Log column values before JSON serialization
            _logger.LogInformation(
                "BuildColumnValues BEFORE JSON: TikCounter={TikCounter}, TikNumber={TikNumber}, BoardId={BoardId}, Count={Count}, ColumnIds={ColumnIds}",
                c.TikCounter,
                c.TikNumber ?? "<null>",
                boardId,
                columnValues.Count,
                string.Join(", ", columnValues.Keys));

            var payloadJson = JsonSerializer.Serialize(columnValues);
            _logger.LogDebug("Monday payload for TikCounter {TikCounter}: {Payload}", c.TikCounter, payloadJson);
            return payloadJson;
        }

        private static void TryAddStringColumn(Dictionary<string, object> columnValues, string? columnId, string? value)
        {
            if (string.IsNullOrWhiteSpace(columnId) || string.IsNullOrWhiteSpace(value))
            {
                return;
            }

            columnValues[columnId] = value;
        }

        private static void TryAddLongTextColumn(Dictionary<string, object> columnValues, string? columnId, string? value)
        {
            if (string.IsNullOrWhiteSpace(columnId) || string.IsNullOrWhiteSpace(value))
            {
                return;
            }

            columnValues[columnId] = new { text = value };
        }

        private static void TryAddDateColumn(Dictionary<string, object> columnValues, string? columnId, DateTime value)
        {
            TryAddDateColumn(columnValues, columnId, (DateTime?)value);
        }

        private static void TryAddDateColumn(Dictionary<string, object> columnValues, string? columnId, DateTime? value)
        {
            if (string.IsNullOrWhiteSpace(columnId) || value is null)
            {
                return;
            }

            columnValues[columnId] = new { date = value.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) };
        }

        private static void TryAddDecimalColumn(Dictionary<string, object> columnValues, string? columnId, decimal? value)
        {
            if (string.IsNullOrWhiteSpace(columnId) || value is null)
            {
                return;
            }

            columnValues[columnId] = value.Value.ToString(CultureInfo.InvariantCulture);
        }

        private void TryAddPhoneColumn(Dictionary<string, object> columnValues, string? columnId, string? phoneNumber, int tikCounter, string context)
        {
            if (string.IsNullOrWhiteSpace(columnId))
            {
                return;
            }

            if (string.IsNullOrWhiteSpace(phoneNumber))
            {
                _logger.LogDebug("No {Context} for TikCounter {TikCounter}; column {ColumnId} left empty", context, tikCounter, columnId);
                return;
            }

            var normalizedForDocument = NormalizeIsraeliPhoneForDocument(phoneNumber);

            string Mask(string? value)
            {
                if (string.IsNullOrWhiteSpace(value))
                {
                    return "<null>";
                }

                var digits = new string(value.Where(char.IsDigit).ToArray());
                if (digits.Length == 0)
                {
                    return "<null>";
                }

                if (digits.Length <= 4)
                {
                    return $"***{digits}";
                }

                return $"***{digits.Substring(digits.Length - 4)}";
            }

            _logger.LogDebug(
                "Phone normalization for {Context} TikCounter {TikCounter}: raw={RawMasked}, normalized={NormalizedMasked}",
                context,
                tikCounter,
                Mask(phoneNumber),
                Mask(normalizedForDocument));

            var payload = BuildPhoneColumnValue(phoneNumber);
            if (payload is null)
            {
                _logger.LogDebug("No {Context} for TikCounter {TikCounter}; column {ColumnId} left empty after normalization", context, tikCounter, columnId);
                return;
            }

            columnValues[columnId] = payload;
            _logger.LogDebug("Including {Context} for TikCounter {TikCounter} on column {ColumnId} with phone={Phone}, countryShortName=IL", context, tikCounter, columnId, payload.phone);
        }

        private static PhoneColumnValue? BuildPhoneColumnValue(string? phoneNumber)
        {
            var normalized = NormalizeIsraeliPhoneForDocument(phoneNumber);
            if (string.IsNullOrWhiteSpace(normalized))
            {
                return null;
            }

            return new PhoneColumnValue
            {
                phone = normalized,
                countryShortName = "IL"
            };
        }

        private sealed class PhoneColumnValue
        {
            public string phone { get; set; } = string.Empty;
            public string countryShortName { get; set; } = "IL";
        }

        private static string? NormalizeIsraeliPhoneForDocument(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                return null;
            }

            var digits = new string(raw.Where(char.IsDigit).ToArray());
            if (string.IsNullOrEmpty(digits))
            {
                return null;
            }

            if (digits.StartsWith("972") && digits.Length > 3)
            {
                // 9725... -> 05...
                digits = "0" + digits.Substring(3);
            }
            else if (digits.StartsWith("0"))
            {
                // Already local format like 054...
                // leave as-is
            }
            // Otherwise leave digits as-is, but never add '+'

            return digits;
        }

        private async Task TryAddHourColumnAsync(
            Dictionary<string, object> columnValues,
            long boardId,
            string? columnId,
            TimeSpan? value,
            int tikCounter,
            CancellationToken ct)
        {
            // Check if column ID is configured
            if (string.IsNullOrWhiteSpace(columnId))
            {
                _logger.LogWarning(
                    "Hearing hour column ID is not configured for TikCounter {TikCounter}. Hearing hour will not be set in Monday.",
                    tikCounter);
                return;
            }

            // Check if value is available
            if (value is null)
            {
                _logger.LogDebug(
                    "Hearing time is null for TikCounter {TikCounter}. Hearing hour column {ColumnId} will not be updated in Monday.",
                    tikCounter,
                    columnId);
                return;
            }

            // Validate that the column exists on the board (optional validation, logs if not found)
            try
            {
                var validationResult = await ValidateHearingHourColumnAsync(boardId, columnId, ct);
                if (!validationResult.IsValid)
                {
                    _logger.LogWarning(
                        "Hearing hour column validation failed for TikCounter {TikCounter} on board {BoardId}: {Reason}. Available columns: {AvailableColumns}. Hearing hour will not be set.",
                        tikCounter,
                        boardId,
                        validationResult.Reason ?? "Column not found",
                        validationResult.AvailableColumnsInfo ?? "unknown");
                    return;
                }

                if (!string.IsNullOrWhiteSpace(validationResult.Warning))
                {
                    _logger.LogWarning(
                        "Hearing hour column {ColumnId} validation warning for TikCounter {TikCounter} on board {BoardId}: {Warning}",
                        columnId,
                        tikCounter,
                        boardId,
                        validationResult.Warning);
                }
            }
            catch (Exception ex)
            {
                // Log but don't fail - column validation is best-effort
                _logger.LogWarning(ex,
                    "Failed to validate hearing hour column {ColumnId} on board {BoardId} for TikCounter {TikCounter}. Will attempt to set value anyway.",
                    columnId,
                    boardId,
                    tikCounter);
            }

            // Set the value as an object with hour and minute properties (Monday.com hour column format)
            // Monday.com requires integers, not strings
            var hour = value.Value.Hours;
            var minute = value.Value.Minutes;

            // Validate hour and minute ranges
            if (hour < 0 || hour > 23)
            {
                _logger.LogWarning(
                    "Invalid hour value {Hour} for TikCounter {TikCounter}. Hour must be between 0 and 23. Hearing hour will not be set.",
                    hour,
                    tikCounter);
                return;
            }

            if (minute < 0 || minute > 59)
            {
                _logger.LogWarning(
                    "Invalid minute value {Minute} for TikCounter {TikCounter}. Minute must be between 0 and 59. Hearing hour will not be set.",
                    minute,
                    tikCounter);
                return;
            }

            // Create the hour value object with integers (Monday.com requirement)
            var hourValue = new
            {
                hour = hour,
                minute = minute
            };
            columnValues[columnId] = hourValue;
            _logger.LogDebug(
                "Set hearing hour {Hour}:{Minute:00} (object format with integers) for TikCounter {TikCounter} on column {ColumnId}",
                hour,
                minute,
                tikCounter,
                columnId);
        }

        private async Task<ColumnValidationResult> ValidateHearingHourColumnAsync(long boardId, string columnId, CancellationToken ct)
        {
            try
            {
                // Try to resolve the column by expected title to verify our configured ID matches
                // The column title on the board is "שעה" (not "שעת דיון")
                string? resolvedColumnId = null;
                string? availableColumnsInfo = null;
                const string expectedColumnTitle = "שעה";
                try
                {
                    resolvedColumnId = await _mondayMetadataProvider.GetColumnIdByTitleAsync(boardId, expectedColumnTitle, ct);
                }
                catch (InvalidOperationException ex)
                {
                    // Extract available columns from the exception message if present
                    var message = ex.Message;
                    if (message.Contains("Available columns:"))
                    {
                        var availableColumnsStart = message.IndexOf("Available columns:", StringComparison.Ordinal);
                        if (availableColumnsStart >= 0)
                        {
                            availableColumnsInfo = message.Substring(availableColumnsStart);
                        }
                    }

                    _logger.LogDebug(
                        "Could not resolve hearing hour column by title '{ExpectedTitle}' on board {BoardId}. {Message}. Will validate by ID {ColumnId} directly.",
                        expectedColumnTitle,
                        boardId,
                        message,
                        columnId);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex,
                        "Exception while resolving hearing hour column by title '{ExpectedTitle}' on board {BoardId}. Will validate by ID {ColumnId} directly.",
                        expectedColumnTitle,
                        boardId,
                        columnId);
                }

                // If we resolved by title and it doesn't match, that's a problem
                if (!string.IsNullOrWhiteSpace(resolvedColumnId) && resolvedColumnId != columnId)
                {
                    return new ColumnValidationResult
                    {
                        IsValid = false,
                        Reason = $"Configured column ID '{columnId}' does not match the column ID resolved by title '{expectedColumnTitle}' ('{resolvedColumnId}')",
                        AvailableColumnsInfo = availableColumnsInfo ?? $"Resolved ID: {resolvedColumnId}, Configured ID: {columnId}"
                    };
                }

                // If resolved ID matches, we're good
                if (resolvedColumnId == columnId)
                {
                    return new ColumnValidationResult { IsValid = true };
                }

                // If we couldn't resolve by title, we can't fully validate, but that's okay
                // The column might exist with a different title or the metadata provider might have issues
                // We'll log this as a debug message and proceed
                _logger.LogDebug(
                    "Could not validate hearing hour column {ColumnId} by title resolution on board {BoardId}. {AvailableColumnsInfo}. Will proceed with configured column ID.",
                    columnId,
                    boardId,
                    availableColumnsInfo ?? "Column metadata unavailable");

                return new ColumnValidationResult
                {
                    IsValid = true,
                    Warning = "Column validation by title resolution unavailable",
                    AvailableColumnsInfo = availableColumnsInfo
                };
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Exception while validating hearing hour column {ColumnId} on board {BoardId}",
                    columnId,
                    boardId);
                // Return valid but with warning - we don't want to block on validation failures
                return new ColumnValidationResult
                {
                    IsValid = true,
                    Warning = $"Validation error: {ex.Message}"
                };
            }
        }

        private class ColumnValidationResult
        {
            public bool IsValid { get; set; }
            public string? Reason { get; set; }
            public string? Warning { get; set; }
            public string? AvailableColumnsInfo { get; set; }
        }

        private class ColumnCacheEntry
        {
            public HashSet<string> ColumnIds { get; set; } = new();
            public DateTime Timestamp { get; set; }
        }

        private void ValidateHourColumnValues(Dictionary<string, object> columnValues, int tikCounter)
        {
            // Check if the hearing hour column is in the payload and validate it
            var hearingHourColumnId = _mondaySettings.HearingHourColumnId;
            if (string.IsNullOrWhiteSpace(hearingHourColumnId))
            {
                return; // No hour column configured, nothing to validate
            }

            if (!columnValues.TryGetValue(hearingHourColumnId, out var hourValue))
            {
                return; // Hour column not in payload, nothing to validate
            }

            // Validate that the value is an object with numeric hour and minute properties
            if (hourValue == null)
            {
                _logger.LogWarning(
                    "Hour column {ColumnId} value is null for TikCounter {TikCounter}. Removing from payload.",
                    hearingHourColumnId,
                    tikCounter);
                columnValues.Remove(hearingHourColumnId);
                return;
            }

            // Serialize only the hour column value to JSON and validate via JsonDocument
            try
            {
                var hourValueJson = JsonSerializer.Serialize(hourValue);
                using var doc = JsonDocument.Parse(hourValueJson);
                var root = doc.RootElement;

                // Validate it's an object (not array, string, number, etc.)
                if (root.ValueKind != JsonValueKind.Object)
                {
                    _logger.LogWarning(
                        "Hour column {ColumnId} value for TikCounter {TikCounter} is not a valid hour object (expected object, got {ValueKind}). Removing from payload.",
                        hearingHourColumnId,
                        tikCounter,
                        root.ValueKind);
                    columnValues.Remove(hearingHourColumnId);
                    return;
                }

                // Validate it contains hour property
                if (!root.TryGetProperty("hour", out var hourElement))
                {
                    _logger.LogWarning(
                        "Hour column {ColumnId} value for TikCounter {TikCounter} is missing 'hour' property. Removing from payload.",
                        hearingHourColumnId,
                        tikCounter);
                    columnValues.Remove(hearingHourColumnId);
                    return;
                }

                // Validate it contains minute property
                if (!root.TryGetProperty("minute", out var minuteElement))
                {
                    _logger.LogWarning(
                        "Hour column {ColumnId} value for TikCounter {TikCounter} is missing 'minute' property. Removing from payload.",
                        hearingHourColumnId,
                        tikCounter);
                    columnValues.Remove(hearingHourColumnId);
                    return;
                }

                // Validate hour is numeric
                if (hourElement.ValueKind != JsonValueKind.Number)
                {
                    _logger.LogWarning(
                        "Hour column {ColumnId} value for TikCounter {TikCounter} has non-numeric hour property (got {ValueKind}). Removing from payload.",
                        hearingHourColumnId,
                        tikCounter,
                        hourElement.ValueKind);
                    columnValues.Remove(hearingHourColumnId);
                    return;
                }

                // Validate minute is numeric
                if (minuteElement.ValueKind != JsonValueKind.Number)
                {
                    _logger.LogWarning(
                        "Hour column {ColumnId} value for TikCounter {TikCounter} has non-numeric minute property (got {ValueKind}). Removing from payload.",
                        hearingHourColumnId,
                        tikCounter,
                        minuteElement.ValueKind);
                    columnValues.Remove(hearingHourColumnId);
                    return;
                }

                // Get integer values
                if (!hourElement.TryGetInt32(out var hour))
                {
                    _logger.LogWarning(
                        "Hour column {ColumnId} value for TikCounter {TikCounter} has hour value that cannot be parsed as integer. Removing from payload.",
                        hearingHourColumnId,
                        tikCounter);
                    columnValues.Remove(hearingHourColumnId);
                    return;
                }

                if (!minuteElement.TryGetInt32(out var minute))
                {
                    _logger.LogWarning(
                        "Hour column {ColumnId} value for TikCounter {TikCounter} has minute value that cannot be parsed as integer. Removing from payload.",
                        hearingHourColumnId,
                        tikCounter);
                    columnValues.Remove(hearingHourColumnId);
                    return;
                }

                // Validate ranges
                if (hour < 0 || hour > 23)
                {
                    _logger.LogWarning(
                        "Hour column {ColumnId} value for TikCounter {TikCounter} has invalid hour {Hour} (must be 0-23). Removing from payload.",
                        hearingHourColumnId,
                        tikCounter,
                        hour);
                    columnValues.Remove(hearingHourColumnId);
                    return;
                }

                if (minute < 0 || minute > 59)
                {
                    _logger.LogWarning(
                        "Hour column {ColumnId} value for TikCounter {TikCounter} has invalid minute {Minute} (must be 0-59). Removing from payload.",
                        hearingHourColumnId,
                        tikCounter,
                        minute);
                    columnValues.Remove(hearingHourColumnId);
                    return;
                }

                // Validation passed - log success at debug level
                _logger.LogDebug(
                    "Hour column {ColumnId} value validated for TikCounter {TikCounter}: hour={Hour}, minute={Minute}",
                    hearingHourColumnId,
                    tikCounter,
                    hour,
                    minute);
            }
            catch (JsonException ex)
            {
                _logger.LogWarning(ex,
                    "Hour column {ColumnId} value for TikCounter {TikCounter} could not be serialized/parsed as JSON. Removing from payload.",
                    hearingHourColumnId,
                    tikCounter);
                columnValues.Remove(hearingHourColumnId);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Unexpected error validating hour column {ColumnId} value for TikCounter {TikCounter}. Removing from payload.",
                    hearingHourColumnId,
                    tikCounter);
                columnValues.Remove(hearingHourColumnId);
            }
        }

        private async Task<HashSet<string>> GetValidColumnIdsAsync(long boardId, CancellationToken ct)
        {
            // Check cache first
            if (_columnIdCache.TryGetValue(boardId, out var cachedEntry))
            {
                var age = DateTime.UtcNow - cachedEntry.Timestamp;
                if (age < ColumnCacheTtl)
                {
                    _logger.LogDebug("Using cached column IDs for board {BoardId} (age: {Age})", boardId, age);
                    return cachedEntry.ColumnIds;
                }
                else
                {
                    _logger.LogDebug("Column ID cache expired for board {BoardId} (age: {Age}), refreshing", boardId, age);
                    _columnIdCache.TryRemove(boardId, out _);
                }
            }

            // Fetch from Monday API
            try
            {
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

                // Use HTTP client with API token from secret provider (same pattern as MondayMetadataProvider)
                using var httpClient = new HttpClient();
                httpClient.BaseAddress = new Uri("https://api.monday.com/v2/");

                var apiToken = _secretProvider.GetSecret("Monday__ApiToken");
                if (string.IsNullOrWhiteSpace(apiToken) || IsPlaceholderValue(apiToken))
                {
                    var fallback = _config["Monday:ApiToken"];
                    if (!string.IsNullOrWhiteSpace(fallback) && !IsPlaceholderValue(fallback))
                    {
                        apiToken = fallback;
                    }
                }

                if (!string.IsNullOrWhiteSpace(apiToken) && !IsPlaceholderValue(apiToken))
                {
                    httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiToken);
                }

                var resp = await httpClient.PostAsync("", content, ct);
                resp.EnsureSuccessStatusCode();
                var body = await resp.Content.ReadAsStringAsync(ct);

                using var doc = JsonDocument.Parse(body);
                var root = doc.RootElement;

                if (root.TryGetProperty("errors", out var errors))
                {
                    _logger.LogWarning("Monday.com API error while fetching column metadata for board {BoardId}: {Errors}", boardId, errors.ToString());
                    throw new InvalidOperationException($"Monday.com API error while fetching column metadata: {errors}");
                }

                if (!root.TryGetProperty("data", out var data) ||
                    !data.TryGetProperty("boards", out var boards) ||
                    boards.ValueKind != JsonValueKind.Array ||
                    boards.GetArrayLength() == 0)
                {
                    throw new InvalidOperationException($"Monday.com unexpected response when fetching board metadata for board {boardId}: {body}");
                }

                var board = boards[0];
                if (!board.TryGetProperty("columns", out var columns) ||
                    columns.ValueKind != JsonValueKind.Array)
                {
                    throw new InvalidOperationException($"Monday.com board {boardId} has no columns in response: {body}");
                }

                var columnIds = new HashSet<string>(StringComparer.Ordinal);
                foreach (var column in columns.EnumerateArray())
                {
                    if (column.TryGetProperty("id", out var columnId))
                    {
                        var columnIdValue = columnId.GetString();
                        if (!string.IsNullOrWhiteSpace(columnIdValue))
                        {
                            columnIds.Add(columnIdValue);
                        }
                    }
                }

                // Cache the result
                var cacheEntry = new ColumnCacheEntry
                {
                    ColumnIds = columnIds,
                    Timestamp = DateTime.UtcNow
                };
                _columnIdCache.AddOrUpdate(boardId, cacheEntry, (key, oldValue) => cacheEntry);

                _logger.LogDebug("Fetched and cached {Count} valid column IDs for board {BoardId}", columnIds.Count, boardId);
                return columnIds;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to fetch valid column IDs for board {BoardId}", boardId);
                throw;
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

        private async Task FilterInvalidColumnsAsync(long boardId, Dictionary<string, object> columnValues, OdcanitCase c, CancellationToken ct)
        {
            if (columnValues.Count == 0)
            {
                return; // Nothing to filter
            }

            try
            {
                var validColumnIds = await GetValidColumnIdsAsync(boardId, ct);
                var removedColumns = new List<string>();

                // Filter out invalid column IDs
                foreach (var columnId in columnValues.Keys.ToList())
                {
                    if (!validColumnIds.Contains(columnId))
                    {
                        var valuePreview = GetValuePreview(columnValues[columnId]);
                        columnValues.Remove(columnId);
                        removedColumns.Add(columnId);

                        _logger.LogWarning(
                            "Removed invalid column ID from payload for TikCounter {TikCounter}, TikNumber {TikNumber}, BoardId {BoardId}: ColumnId={ColumnId}, ValuePreview={ValuePreview}",
                            c.TikCounter,
                            c.TikNumber ?? "<null>",
                            boardId,
                            columnId,
                            valuePreview);

                        await _skipLogger.LogSkipAsync(
                            c.TikCounter,
                            c.TikNumber,
                            operation: "MondayColumnFilter",
                            reasonCode: "InvalidColumnId",
                            entityId: columnId,
                            rawValue: valuePreview,
                            details: new
                            {
                                BoardId = boardId,
                                CaseTikNumber = c.TikNumber,
                                CaseClientName = c.ClientName
                            },
                            ct);
                    }
                }

                if (removedColumns.Count > 0)
                {
                    _logger.LogWarning(
                        "Removed {Count} invalid column ID(s) from payload for TikCounter {TikCounter}, TikNumber {TikNumber}, BoardId {BoardId}: {ColumnIds}",
                        removedColumns.Count,
                        c.TikCounter,
                        c.TikNumber ?? "<null>",
                        boardId,
                        string.Join(", ", removedColumns));
                }

                // Check for missing critical columns
                await ValidateCriticalColumnsAsync(boardId, validColumnIds, c, ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Failed to filter invalid columns for TikCounter {TikCounter}, TikNumber {TikNumber}, BoardId {BoardId}. Proceeding with original payload.",
                    c.TikCounter,
                    c.TikNumber ?? "<null>",
                    boardId);
                // Continue with original payload if filtering fails
            }
        }

        private static string GetValuePreview(object? value)
        {
            if (value == null)
            {
                return "<null>";
            }

            try
            {
                var json = JsonSerializer.Serialize(value);
                if (json.Length > 100)
                {
                    return json.Substring(0, 97) + "...";
                }
                return json;
            }
            catch
            {
                return value.ToString() ?? "<unknown>";
            }
        }

        private async Task ValidateCriticalColumnsAsync(long boardId, HashSet<string> validColumnIds, OdcanitCase c, CancellationToken ct)
        {
            var criticalColumns = GetCriticalColumnIds();
            if (criticalColumns.Count == 0)
            {
                return; // No critical columns configured
            }

            var missingCriticalColumns = new List<string>();
            foreach (var criticalColumnId in criticalColumns)
            {
                if (string.IsNullOrWhiteSpace(criticalColumnId))
                {
                    continue;
                }

                if (!validColumnIds.Contains(criticalColumnId))
                {
                    missingCriticalColumns.Add(criticalColumnId);
                }
            }

            if (missingCriticalColumns.Count > 0)
            {
                var failOnMissing = _config.GetValue<bool>("Monday:FailOnMissingCriticalColumns", false);
                var logLevel = failOnMissing ? LogLevel.Error : LogLevel.Warning;

                _logger.Log(logLevel,
                    "Critical column(s) missing from board {BoardId} for TikCounter {TikCounter}, TikNumber {TikNumber}: {MissingColumns}. FailOnMissingCriticalColumns={FailOnMissing}",
                    boardId,
                    c.TikCounter,
                    c.TikNumber ?? "<null>",
                    string.Join(", ", missingCriticalColumns),
                    failOnMissing);

                if (failOnMissing)
                {
                    throw new InvalidOperationException(
                        $"Critical column(s) missing from Monday board {boardId}: {string.Join(", ", missingCriticalColumns)}. " +
                        $"TikCounter={c.TikCounter}, TikNumber={c.TikNumber ?? "<null>"}");
                }
            }
        }

        private List<string> GetCriticalColumnIds()
        {
            var criticalColumns = new List<string>();

            // Add configured critical columns from settings
            if (!string.IsNullOrWhiteSpace(_mondaySettings.CaseNumberColumnId))
            {
                criticalColumns.Add(_mondaySettings.CaseNumberColumnId);
            }

            if (!string.IsNullOrWhiteSpace(_mondaySettings.HearingDateColumnId))
            {
                criticalColumns.Add(_mondaySettings.HearingDateColumnId);
            }

            if (!string.IsNullOrWhiteSpace(_mondaySettings.HearingHourColumnId))
            {
                criticalColumns.Add(_mondaySettings.HearingHourColumnId);
            }

            if (!string.IsNullOrWhiteSpace(_mondaySettings.CourtNameStatusColumnId))
            {
                criticalColumns.Add(_mondaySettings.CourtNameStatusColumnId);
            }

            // Allow additional critical columns from configuration
            var configCriticalColumns = _config.GetSection("Monday:CriticalColumns").Get<List<string>>();
            if (configCriticalColumns != null && configCriticalColumns.Count > 0)
            {
                criticalColumns.AddRange(configCriticalColumns.Where(c => !string.IsNullOrWhiteSpace(c)));
            }

            return criticalColumns.Distinct().ToList();
        }

        private static void TryAddStatusLabelColumn(Dictionary<string, object> columnValues, string? columnId, string? label)
        {
            if (string.IsNullOrWhiteSpace(columnId) || string.IsNullOrWhiteSpace(label))
            {
                return;
            }

            columnValues[columnId] = new { label };
        }

        private static void TryAddDropdownColumn(Dictionary<string, object> columnValues, string? columnId, string? value)
        {
            if (string.IsNullOrWhiteSpace(columnId) || string.IsNullOrWhiteSpace(value))
            {
                return;
            }

            columnValues[columnId] = new { labels = new[] { value } };
        }

        private async Task TryAddClientNumberDropdownAsync(
            Dictionary<string, object> columnValues,
            long boardId,
            string? columnId,
            string? clientNumber,
            int tikCounter,
            string? tikNumber,
            CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(columnId))
            {
                return;
            }

            if (string.IsNullOrWhiteSpace(clientNumber))
            {
                _logger.LogDebug(
                    "ClientNumber is null/empty for TikCounter {TikCounter}, TikNumber {TikNumber}; dropdown column {ColumnId} will be omitted.",
                    tikCounter,
                    tikNumber ?? "<null>",
                    columnId);
                return;
            }

            var trimmedClientNumber = clientNumber.Trim();

            try
            {
                var allowedLabels = await _mondayMetadataProvider.GetAllowedDropdownLabelsAsync(boardId, columnId, ct);

                if (!allowedLabels.Contains(trimmedClientNumber))
                {
                    _logger.LogWarning(
                        "ClientNumber '{ClientNumber}' is not a valid label for dropdown column {ColumnId} on board {BoardId}. TikCounter={TikCounter}, TikNumber={TikNumber}. Column will be omitted.",
                        trimmedClientNumber,
                        columnId,
                        boardId,
                        tikCounter,
                        tikNumber ?? "<null>");

                    await _skipLogger.LogSkipAsync(
                        tikCounter,
                        tikNumber,
                        operation: "MondayColumnValueValidation",
                        reasonCode: "monday_invalid_dropdown_value",
                        entityId: columnId,
                        rawValue: trimmedClientNumber,
                        details: new
                        {
                            EntityType = "Dropdown",
                            BoardId = boardId,
                            AllowedLabelCount = allowedLabels.Count
                        },
                        ct);

                    return;
                }

                columnValues[columnId] = new { labels = new[] { trimmedClientNumber } };
                _logger.LogDebug(
                    "Including ClientNumber '{ClientNumber}' for TikCounter {TikCounter}, TikNumber {TikNumber} in dropdown column {ColumnId} on board {BoardId}.",
                    trimmedClientNumber,
                    tikCounter,
                    tikNumber ?? "<null>",
                    columnId,
                    boardId);
            }
            catch (Exception ex)
            {
                // Non-critical field metadata failure - log warning and skip field, continue sync
                _logger.LogWarning(
                    ex,
                    "Failed to fetch/validate metadata for non-critical dropdown column {ColumnId} on board {BoardId}. ClientNumber '{ClientNumber}' for TikCounter {TikCounter}, TikNumber {TikNumber} will be omitted from this sync. Exception: {Message}",
                    columnId,
                    boardId,
                    trimmedClientNumber,
                    tikCounter,
                    tikNumber ?? "<null>",
                    ex.Message);
            }
        }

        private static string MapTaskTypeLabel(string? tikType)
        {
            if (string.IsNullOrWhiteSpace(tikType))
            {
                return "טיפול בתיק";
            }

            var value = tikType.Trim();
            if (value.IndexOf("פגיש", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return "פגישה";
            }

            if (value.IndexOf("מכתב", StringComparison.OrdinalIgnoreCase) >= 0 ||
                value.IndexOf("דריש", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return "מכתב דרישה";
            }

            if (value.IndexOf("זימון", StringComparison.OrdinalIgnoreCase) >= 0 ||
                value.IndexOf("דיון", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return "זימון לדיון";
            }

            if (value.IndexOf("הודע", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return "הודעה";
            }

            return "טיפול בתיק";
        }

        /// <summary>
        /// Determines DocumentType from ClientVisualID based on strict business rules.
        /// DocumentType does NOT exist in Odcanit DB and must be derived deterministically.
        /// ClientNumber is defined as the substring before '\' (backslash) if present, otherwise the entire ClientVisualID.
        /// </summary>
        /// <param name="clientVisualID">ClientVisualID in format "ClientNumber\OtherData", e.g. "102\5334"</param>
        /// <returns>DocumentType status label</returns>
        /// <exception cref="InvalidOperationException">Thrown if ClientVisualID is invalid or cannot be parsed (critical field)</exception>
        private static string DetermineDocumentTypeFromClientVisualId(string? clientVisualID)
        {
            if (string.IsNullOrWhiteSpace(clientVisualID))
            {
                throw new InvalidOperationException(
                    "Cannot determine DocumentType: ClientVisualID is null or empty. DocumentType is a critical field and must be derived from ClientVisualID.");
            }

            // Extract ClientNumber (substring before '\' if present, otherwise entire string)
            var backslashIndex = clientVisualID.IndexOf('\\');
            string clientNumberStr;
            
            if (backslashIndex > 0)
            {
                clientNumberStr = clientVisualID.Substring(0, backslashIndex).Trim();
            }
            else
            {
                // No backslash found, use entire string
                clientNumberStr = clientVisualID.Trim();
            }

            if (!int.TryParse(clientNumberStr, out var clientNumber))
            {
                throw new InvalidOperationException(
                    $"Cannot determine DocumentType: Failed to parse ClientNumber from ClientVisualID '{clientVisualID}'. Extracted: '{clientNumberStr}'. DocumentType is a critical field.");
            }

            // Apply business rules
            // Rule 1: ClientNumber 1, 2, 5, 8 → "כתב הגנה"
            if (clientNumber == 1 || clientNumber == 2 || clientNumber == 5 || clientNumber == 8)
            {
                return "כתב הגנה";
            }

            // Rule 2: ClientNumber 6 → "מכתב דרישה אילי"
            if (clientNumber == 6)
            {
                return "מכתב דרישה אילי";
            }

            // Rule 3: ClientNumber 7, 9, 4 → "כתב תביעה"
            if (clientNumber == 7 || clientNumber == 9 || clientNumber == 4)
            {
                return "כתב תביעה";
            }

            // Rule 4: ClientNumber >= 100 (3+ digits) → "כתב תביעה"
            if (clientNumber >= 100)
            {
                return "כתב תביעה";
            }

            // If no rule matches, this is an unexpected ClientNumber - fail loudly
            throw new InvalidOperationException(
                $"Cannot determine DocumentType: ClientNumber {clientNumber} (from ClientVisualID '{clientVisualID}') does not match any known business rule. DocumentType is a critical field.");
        }

        private static string? DetermineResponsibleText(OdcanitCase c)
        {
            if (!string.IsNullOrWhiteSpace(c.Referant))
            {
                return c.Referant;
            }

            if (!string.IsNullOrWhiteSpace(c.TeamName))
            {
                return c.TeamName;
            }

            if (c.TikOwner.HasValue)
            {
                return c.TikOwner.Value.ToString(CultureInfo.InvariantCulture);
            }

            return null;
        }

        private static string? MapPlaintiffSideLabel(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                return null;
            }

            return raw switch
            {
                "תובע" => "תובע",
                "תובעת" => "תובעת",
                "תובעים" => "תובעים",
                "תובעות" => "תובעות",
                _ => null
            };
        }

        private static string? MapDefendantSideLabel(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                return null;
            }

            return raw switch
            {
                "נתבע" => "נתבע",
                "נתבעת" => "נתבעת",
                "נתבעים" => "נתבעים",
                "נתבעות" => "נתבעות",
                _ => null
            };
        }

        private string ResolveClientPhoneColumnId()
        {
            return string.IsNullOrWhiteSpace(_mondaySettings.ClientPhoneColumnId)
                ? DefaultClientPhoneColumnId
                : _mondaySettings.ClientPhoneColumnId!;
        }

        private string ResolveClientEmailColumnId()
        {
            return string.IsNullOrWhiteSpace(_mondaySettings.ClientEmailColumnId)
                ? DefaultClientEmailColumnId
                : _mondaySettings.ClientEmailColumnId!;
        }

        private static bool IsMappingTestCompatible(MondayItemMapping mapping)
        {
            if (mapping.MondayChecksum is null)
            {
                return false;
            }

            return mapping.MondayChecksum.StartsWith("[TEST] ", StringComparison.Ordinal);
        }

        private static int MapStatusIndex(string status)
        {
            if (string.IsNullOrWhiteSpace(status)) return 5;
            var s = status.ToLowerInvariant();

            if (s.Contains("סגור") || s.Contains("closed")) return 1;
            if (s.Contains("פתוח") || s.Contains("open") || s.Contains("עבודה")) return 0;
            if (s.Contains("תקוע") || s.Contains("stuck")) return 2;

            return 5;
        }

        public Task SyncMondayToOdcanitAsync(CancellationToken ct)
        {
            // Not implemented yet; for two-way sync later.
            throw new NotImplementedException();
        }

        private async Task<MondayItemMapping?> FindOrCreateMappingAsync(
            OdcanitCase c,
            long boardId,
            string itemName,
            bool testMode,
            bool dryRun,
            CancellationToken ct)
        {
            MondayItemMapping? mapping = null;
            string lookupMethod = "none";

            // Priority 1: Find mapping by TikCounter + BoardId (source of truth)
                mapping = await _integrationDb.MondayItemMappings
                .FirstOrDefaultAsync(m => m.TikCounter == c.TikCounter && m.BoardId == boardId, ct);
                
                if (mapping != null)
                {
                lookupMethod = "mapping_by_tikcounter_boardid";
                    _logger.LogDebug(
                    "Found mapping by TikCounter+BoardId: TikCounter={TikCounter}, BoardId={BoardId}, MondayItemId={MondayItemId}, TikNumber={TikNumber}",
                    c.TikCounter, boardId, mapping.MondayItemId, mapping.TikNumber ?? "<null>");
            }

            // Priority 2: Fallback to mapping by TikCounter only (for legacy mappings)
            if (mapping == null)
            {
                mapping = await _integrationDb.MondayItemMappings
                    .FirstOrDefaultAsync(m => m.TikCounter == c.TikCounter, ct);
                
                if (mapping != null)
                {
                    lookupMethod = "mapping_by_tikcounter_fallback";
                    _logger.LogDebug(
                        "Found mapping by TikCounter fallback: TikCounter={TikCounter}, MondayItemId={MondayItemId}, ExistingTikNumber={ExistingTikNumber}, BoardId={BoardId}",
                        c.TikCounter, mapping.MondayItemId, mapping.TikNumber ?? "<null>", mapping.BoardId);
                    
                    // Verify the Monday item has the correct TikNumber if we have one
                    if (!string.IsNullOrWhiteSpace(c.TikNumber) && !dryRun)
                    {
                        // If mapping has a null or different TikNumber, we need to check Monday to see which is correct
                        bool needsVerification = string.IsNullOrWhiteSpace(mapping.TikNumber) || mapping.TikNumber != c.TikNumber;
                        
                        if (needsVerification)
                        {
                            if (!string.IsNullOrWhiteSpace(mapping.TikNumber))
                            {
                                _logger.LogWarning(
                                    "Mapping found by TikCounter has different TikNumber: MappingTikNumber={MappingTikNumber}, CaseTikNumber={CaseTikNumber}, MondayItemId={MondayItemId}. Will verify via Monday API.",
                                    mapping.TikNumber, c.TikNumber, mapping.MondayItemId);
                            }
                            else
                            {
                                _logger.LogDebug(
                                    "Mapping found by TikCounter has null TikNumber. CaseTikNumber={CaseTikNumber}, MondayItemId={MondayItemId}. Will verify via Monday API.",
                                    c.TikNumber, mapping.MondayItemId);
                            }
                            
                            // Check if there's a Monday item with the correct TikNumber
                            try
                            {
                                var correctItemId = await _mondayClient.FindItemIdByColumnValueAsync(
                                    boardId,
                                    _mondaySettings.CaseNumberColumnId!,
                                    c.TikNumber,
                                    ct);
                                
                                if (correctItemId.HasValue && correctItemId.Value != mapping.MondayItemId)
                                {
                                    // Found a different item with the correct TikNumber - use that instead
                                    _logger.LogInformation(
                                        "Found different Monday item with correct TikNumber: ExistingMappingItemId={ExistingItemId}, CorrectItemId={CorrectItemId}, TikNumber={TikNumber}. Updating mapping.",
                                        mapping.MondayItemId, correctItemId.Value, c.TikNumber);
                                    
                                    // Update the mapping to point to the correct item
                                    mapping.MondayItemId = correctItemId.Value;
                                    mapping.TikNumber = c.TikNumber;
                                    mapping.BoardId = boardId;
                                    lookupMethod = "mapping_updated_via_api_verification";
                                }
                                else if (correctItemId.HasValue && correctItemId.Value == mapping.MondayItemId)
                                {
                                    // The mapping points to the correct item, just update TikNumber
                                    _logger.LogInformation(
                                        "Mapping points to correct Monday item. Updating TikNumber: TikNumber={TikNumber}, MondayItemId={MondayItemId}",
                                        c.TikNumber, mapping.MondayItemId);
                                    mapping.TikNumber = c.TikNumber;
                                    mapping.BoardId = boardId;
                                    lookupMethod = "mapping_verified_and_updated";
                                }
                                else
                                {
                                    // No item found with correct TikNumber - update mapping with TikNumber from case
                                    _logger.LogDebug(
                                        "No Monday item found with TikNumber={TikNumber}. Updating mapping with case TikNumber. MondayItemId={MondayItemId}",
                                        c.TikNumber, mapping.MondayItemId);
                                    mapping.TikNumber = c.TikNumber;
                                    mapping.BoardId = boardId;
                                    lookupMethod = "mapping_updated_with_tiknumber";
                                }
                            }
                            catch (Exception ex)
                            {
                                _logger.LogWarning(ex, "Failed to verify Monday item TikNumber for mapping. Will use existing mapping and update TikNumber.");
                                // Still update the mapping with TikNumber even if verification failed
                                if (string.IsNullOrWhiteSpace(mapping.TikNumber))
                                {
                                    mapping.TikNumber = c.TikNumber;
                                    mapping.BoardId = boardId;
                                }
                            }
                        }
                    }
                }
            }

            // Priority 3: If no mapping found and we have TikNumber, search Monday API for existing item
            if (mapping == null && !string.IsNullOrWhiteSpace(c.TikNumber) && !dryRun)
            {
                try
                {
                    var existingItemId = await _mondayClient.FindItemIdByColumnValueAsync(
                        boardId,
                        _mondaySettings.CaseNumberColumnId!,
                        c.TikNumber,
                        ct);

                    if (existingItemId.HasValue)
                    {
                        // Found existing item in Monday - create mapping for it
                        mapping = new MondayItemMapping
                        {
                            TikCounter = c.TikCounter,
                            TikNumber = c.TikNumber,
                            BoardId = boardId,
                            MondayItemId = existingItemId.Value,
                            LastSyncFromOdcanitUtc = DateTime.UtcNow,
                            OdcanitVersion = c.tsModifyDate?.ToString("o") ?? string.Empty,
                            MondayChecksum = itemName,
                            HearingChecksum = ComputeHearingChecksum(c),
                            IsTest = testMode
                        };
                        _integrationDb.MondayItemMappings.Add(mapping);
                        await _integrationDb.SaveChangesAsync(ct);
                        lookupMethod = "api_lookup_created_mapping";
                        _logger.LogInformation(
                            "Found existing Monday item via API lookup and created mapping: TikNumber={TikNumber}, BoardId={BoardId}, MondayItemId={MondayItemId}, TikCounter={TikCounter}",
                            c.TikNumber, boardId, existingItemId.Value, c.TikCounter);
                    }
                    else
                    {
                        _logger.LogDebug("No existing Monday item found for TikNumber={TikNumber} on BoardId={BoardId}. Will create new item.", c.TikNumber, boardId);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to query Monday API for existing item with TikNumber {TikNumber}, will create new item", c.TikNumber);
                }
            }

            // Log the lookup method used for this case
            if (mapping != null)
            {
                _logger.LogDebug(
                    "Mapping lookup result: Method={LookupMethod}, TikNumber={TikNumber}, TikCounter={TikCounter}, MondayItemId={MondayItemId}",
                    lookupMethod, c.TikNumber, c.TikCounter, mapping.MondayItemId);
            }
            else
            {
                _logger.LogDebug(
                    "No mapping found: TikNumber={TikNumber}, TikCounter={TikCounter}. Will create new Monday item.",
                    c.TikNumber, c.TikCounter);
            }

            return mapping;
        }

        private SyncAction DetermineSyncAction(MondayItemMapping? mapping, OdcanitCase c, long boardId, string itemName)
        {
            if (mapping == null)
            {
                _logger.LogDebug(
                    "DetermineSyncAction: No mapping found for TikNumber={TikNumber}, TikCounter={TikCounter}. Action=create",
                    c.TikNumber, c.TikCounter);
                return new SyncAction { Action = "create" };
            }

            // Update mapping with TikNumber and BoardId if missing (for legacy mappings)
            bool mappingUpdated = false;
            if (string.IsNullOrWhiteSpace(mapping.TikNumber) && !string.IsNullOrWhiteSpace(c.TikNumber))
            {
                mapping.TikNumber = c.TikNumber;
                mappingUpdated = true;
                _logger.LogDebug(
                    "Updated mapping with TikNumber: TikCounter={TikCounter}, TikNumber={TikNumber}, MondayItemId={MondayItemId}",
                    c.TikCounter, c.TikNumber, mapping.MondayItemId);
            }
            if (mapping.BoardId == 0)
            {
                mapping.BoardId = boardId;
                mappingUpdated = true;
                _logger.LogDebug(
                    "Updated mapping with BoardId: TikCounter={TikCounter}, BoardId={BoardId}, MondayItemId={MondayItemId}",
                    c.TikCounter, boardId, mapping.MondayItemId);
            }

            var odcanitVersion = c.tsModifyDate?.ToString("o") ?? string.Empty;
            var requiresDataUpdate = mapping.OdcanitVersion != odcanitVersion;
            var requiresNameUpdate = mapping.MondayChecksum != itemName;

            // Hearing checksum: detect hearing-only changes even when main case data is unchanged
            var currentHearingChecksum = ComputeHearingChecksum(c);
            var requiresHearingUpdate = mapping.HearingChecksum != currentHearingChecksum;

            if (requiresHearingUpdate && !requiresDataUpdate && !requiresNameUpdate)
            {
                _logger.LogInformation(
                    "Hearing-only change detected -> forcing update: TikCounter={TikCounter}, TikNumber={TikNumber}, MondayItemId={MondayItemId}, OldHearingChecksum={OldChecksum}, NewHearingChecksum={NewChecksum}",
                    c.TikCounter, c.TikNumber, mapping.MondayItemId,
                    mapping.HearingChecksum ?? "<null>", currentHearingChecksum);
            }

            if (!requiresDataUpdate && !requiresNameUpdate && !requiresHearingUpdate)
            {
                if (mappingUpdated)
                {
                    // Even if no data/name update needed, we should save the mapping updates
                    _logger.LogDebug(
                        "DetermineSyncAction: Mapping metadata updated but no data changes. TikNumber={TikNumber}, TikCounter={TikCounter}. Action=skip (mapping will be saved)",
                        c.TikNumber, c.TikCounter);
                }
                else
                {
                    _logger.LogDebug(
                        "DetermineSyncAction: No changes detected. TikNumber={TikNumber}, TikCounter={TikCounter}, MondayItemId={MondayItemId}. Action=skip",
                        c.TikNumber, c.TikCounter, mapping.MondayItemId);
                }
                return new SyncAction { Action = "skip" };
            }

            _logger.LogDebug(
                "DetermineSyncAction: Changes detected. TikNumber={TikNumber}, TikCounter={TikCounter}, MondayItemId={MondayItemId}, RequiresDataUpdate={RequiresDataUpdate}, RequiresNameUpdate={RequiresNameUpdate}, RequiresHearingUpdate={RequiresHearingUpdate}. Action=update",
                c.TikNumber, c.TikCounter, mapping.MondayItemId, requiresDataUpdate, requiresNameUpdate, requiresHearingUpdate);

            return new SyncAction
            {
                Action = "update",
                RequiresDataUpdate = requiresDataUpdate,
                RequiresNameUpdate = requiresNameUpdate,
                RequiresHearingUpdate = requiresHearingUpdate
            };
        }

        private async Task<long> CreateMondayItemAsync(
            OdcanitCase c,
            long boardId,
            string groupId,
            string itemName,
            bool testMode,
            CancellationToken ct)
        {
            _logger.LogDebug(
                "Court mapping for TikCounter {TikCounter}, TikNumber {TikNumber}: CourtCaseNumber -> {CourtCaseNumber}, CourtCity -> {CourtCity}",
                c.TikCounter,
                c.TikNumber ?? "<null>",
                c.CourtCaseNumber ?? "<null>",
                c.CourtCity ?? "<null>");

            // FAIL-FAST: Validate critical fields before creating Monday item
            await ValidateCriticalFieldsAsync(boardId, c, ct);

            var columnValuesJson = await BuildColumnValuesJsonAsync(boardId, c, forceNotStartedStatus: true, ct);
            var mondayItemId = await _mondayClient.CreateItemAsync(boardId, groupId, itemName, columnValuesJson, ct);

            var newMapping = new MondayItemMapping
            {
                TikCounter = c.TikCounter,
                TikNumber = c.TikNumber,
                BoardId = boardId,
                MondayItemId = mondayItemId,
                LastSyncFromOdcanitUtc = DateTime.UtcNow,
                            OdcanitVersion = c.tsModifyDate?.ToString("o") ?? string.Empty,
                MondayChecksum = itemName,
                            HearingChecksum = ComputeHearingChecksum(c),
                IsTest = testMode
            };
            _integrationDb.MondayItemMappings.Add(newMapping);

            return mondayItemId;
        }

        private async Task UpdateMondayItemAsync(
            MondayItemMapping mapping,
            OdcanitCase c,
            long boardId,
            string itemName,
            bool requiresNameUpdate,
            bool requiresDataUpdate,
            bool requiresHearingUpdate,
            bool testMode,
            CancellationToken ct)
        {
            // Update item name if it has changed
            if (requiresNameUpdate)
            {
                await _mondayClient.UpdateItemNameAsync(boardId, mapping.MondayItemId, itemName, ct);
            }

            // Update column values if data or hearing has changed
            if (requiresDataUpdate || requiresHearingUpdate)
            {
                _logger.LogDebug(
                    "Court mapping for TikCounter {TikCounter}, TikNumber {TikNumber}: CourtCaseNumber -> {CourtCaseNumber}, CourtCity -> {CourtCity}",
                    c.TikCounter,
                    c.TikNumber ?? "<null>",
                    c.CourtCaseNumber ?? "<null>",
                    c.CourtCity ?? "<null>");

                // FAIL-FAST: Validate critical fields before updating Monday item
                await ValidateCriticalFieldsAsync(boardId, c, ct);

                var columnValuesJson = await BuildColumnValuesJsonAsync(boardId, c, forceNotStartedStatus: false, ct);
                await _mondayClient.UpdateItemAsync(boardId, mapping.MondayItemId, columnValuesJson, ct);
                mapping.OdcanitVersion = c.tsModifyDate?.ToString("o") ?? string.Empty;
            }

            // Update mapping metadata (including TikNumber and BoardId if they were missing)
            if (string.IsNullOrWhiteSpace(mapping.TikNumber) && !string.IsNullOrWhiteSpace(c.TikNumber))
            {
                mapping.TikNumber = c.TikNumber;
            }
            if (mapping.BoardId == 0)
            {
                mapping.BoardId = boardId;
            }
            mapping.LastSyncFromOdcanitUtc = DateTime.UtcNow;
            mapping.MondayChecksum = itemName;
            // Always persist the current hearing checksum
            mapping.HearingChecksum = ComputeHearingChecksum(c);
            mapping.IsTest = testMode;
        }

        private class SyncAction
        {
            public string Action { get; set; } = string.Empty; // "create", "update", or "skip"
            public bool RequiresNameUpdate { get; set; }
            public bool RequiresDataUpdate { get; set; }
            public bool RequiresHearingUpdate { get; set; }
        }

        // Critical columns that require strict validation (fail-fast)
        // NOTE: Column type is now detected dynamically from Monday metadata, not hardcoded
        private static readonly List<CriticalColumnDefinition> CriticalColumns = new()
        {
            new CriticalColumnDefinition
            {
                FieldName = "DocumentType",
                GetValue = c => c.DocumentType,
                ValidationMessage = "Document type (סוג מסמך) is critical - prevents automatic creation of wrong document types (e.g., 'כתב תביעה' vs 'כתב הגנה')"
            },
            new CriticalColumnDefinition
            {
                FieldName = "PlaintiffSide",
                GetValue = c => c.PlaintiffSideRaw,
                ValidationMessage = "Plaintiff side (צד תובע) is critical - prevents incorrect party designation"
            },
            new CriticalColumnDefinition
            {
                FieldName = "DefendantSide",
                GetValue = c => c.DefendantSideRaw,
                ValidationMessage = "Defendant side (צד נתבע) is critical - prevents incorrect party designation"
            }
        };

        private async Task ValidateCriticalFieldsAsync(long boardId, OdcanitCase c, CancellationToken ct)
        {
            // Special handling for Client 6: Skip DocumentType validation
            // Client 6 uses "מכתב דרישה אילי" which doesn't exist in Monday labels
            var isClient6 = IsClient6(c.ClientVisualID);
            
            foreach (var criticalColumn in CriticalColumns)
            {
                // Skip DocumentType validation for Client 6
                if (isClient6 && criticalColumn.FieldName == "DocumentType")
                {
                    _logger.LogDebug(
                        "Client 6 special handling: Skipping DocumentType validation for TikCounter={TikCounter}, TikNumber={TikNumber}",
                        c.TikCounter,
                        c.TikNumber ?? "<null>");
                    continue;
                }
                
                var fieldValue = criticalColumn.GetValue(c);
                
                // Check if value is null or empty
                if (string.IsNullOrWhiteSpace(fieldValue))
                {
                    var columnId = GetColumnIdForField(criticalColumn.FieldName);
                    _logger.LogError(
                        "CRITICAL FIELD VALIDATION FAILED: TikCounter={TikCounter}, TikNumber={TikNumber}, Field={FieldName}, ColumnId={ColumnId}, Value=<null/empty>, Reason=MISSING_VALUE. {ValidationMessage}",
                        c.TikCounter,
                        c.TikNumber ?? "<null>",
                        criticalColumn.FieldName,
                        columnId ?? "<unknown>",
                        criticalColumn.ValidationMessage);

                    throw new CriticalFieldValidationException(
                        c.TikCounter,
                        c.TikNumber,
                        columnId ?? "<unknown>",
                        fieldValue,
                        $"MISSING_VALUE - {criticalColumn.ValidationMessage}");
                }

                // Check if value exists in Monday column labels
                var columnId2 = GetColumnIdForField(criticalColumn.FieldName);
                if (string.IsNullOrWhiteSpace(columnId2))
                {
                    _logger.LogError(
                        "CRITICAL FIELD VALIDATION FAILED: TikCounter={TikCounter}, TikNumber={TikNumber}, Field={FieldName}, ColumnId=<missing>, Reason=CONFIG_MISSING_COLUMN_ID. Column ID not configured in MondaySettings for critical field.",
                        c.TikCounter,
                        c.TikNumber ?? "<null>",
                        criticalColumn.FieldName);

                    throw new CriticalFieldValidationException(
                        c.TikCounter,
                        c.TikNumber,
                        "<missing>",
                        fieldValue,
                        $"CONFIG_MISSING_COLUMN_ID - Column ID not configured in MondaySettings for critical field {criticalColumn.FieldName}");
                }

                // Detect actual column type from Monday metadata
                // Note: This will THROW on infrastructure failures instead of silently skipping
                var actualColumnType = await _mondayMetadataProvider.GetColumnTypeAsync(boardId, columnId2, ct);
                if (string.IsNullOrWhiteSpace(actualColumnType))
                {
                    _logger.LogError(
                        "CRITICAL FIELD VALIDATION FAILED: TikCounter={TikCounter}, TikNumber={TikNumber}, Field={FieldName}, ColumnId={ColumnId}, Reason=METADATA_MISSING_COLUMN_TYPE. Cannot detect column type for critical field.",
                        c.TikCounter,
                        c.TikNumber ?? "<null>",
                        criticalColumn.FieldName,
                        columnId2);

                    throw new CriticalFieldValidationException(
                        c.TikCounter,
                        c.TikNumber,
                        columnId2,
                        fieldValue,
                        $"METADATA_MISSING_COLUMN_TYPE - Cannot detect column type for critical field {criticalColumn.FieldName} (ColumnId={columnId2})");
                }

                _logger.LogDebug(
                    "Detected column type for critical field {FieldName} (ColumnId={ColumnId}): {ColumnType}",
                    criticalColumn.FieldName,
                    columnId2,
                    actualColumnType);

                // Fetch allowed labels based on detected column type
                // Note: This will now THROW on infrastructure failures (auth, network, config)
                // instead of silently returning empty labels
                HashSet<string> allowedLabels;
                if (actualColumnType == "color" || actualColumnType == "status")
                {
                    // Status columns (Monday uses "color" type for status columns)
                    allowedLabels = await _mondayMetadataProvider.GetAllowedStatusLabelsAsync(boardId, columnId2, ct);
                }
                else if (actualColumnType == "dropdown")
                {
                    // Dropdown columns
                    allowedLabels = await _mondayMetadataProvider.GetAllowedDropdownLabelsAsync(boardId, columnId2, ct);
                }
                else
                {
                    _logger.LogError(
                        "CRITICAL FIELD VALIDATION FAILED: TikCounter={TikCounter}, TikNumber={TikNumber}, Field={FieldName}, ColumnId={ColumnId}, ColumnType={ColumnType}, Reason=UNSUPPORTED_COLUMN_TYPE. Critical field has unsupported column type.",
                        c.TikCounter,
                        c.TikNumber ?? "<null>",
                        criticalColumn.FieldName,
                        columnId2,
                        actualColumnType);

                    throw new CriticalFieldValidationException(
                        c.TikCounter,
                        c.TikNumber,
                        columnId2,
                        fieldValue,
                        $"UNSUPPORTED_COLUMN_TYPE - Critical field {criticalColumn.FieldName} has unsupported column type '{actualColumnType}' (expected: color, status, or dropdown). ColumnId={columnId2}");
                }

                if (!allowedLabels.Contains(fieldValue))
                {
                    _logger.LogError(
                        "CRITICAL FIELD VALIDATION FAILED: TikCounter={TikCounter}, TikNumber={TikNumber}, Field={FieldName}, ColumnId={ColumnId}, ColumnType={ColumnType}, Value='{Value}', Reason=INVALID_LABEL, AllowedLabels=[{AllowedLabels}]. {ValidationMessage}",
                        c.TikCounter,
                        c.TikNumber ?? "<null>",
                        criticalColumn.FieldName,
                        columnId2,
                        actualColumnType,
                        fieldValue,
                        string.Join(", ", allowedLabels),
                        criticalColumn.ValidationMessage);

                    throw new CriticalFieldValidationException(
                        c.TikCounter,
                        c.TikNumber,
                        columnId2,
                        fieldValue,
                        $"INVALID_LABEL - Value '{fieldValue}' not in allowed labels for {actualColumnType} column: [{string.Join(", ", allowedLabels)}]. {criticalColumn.ValidationMessage}");
                }

                _logger.LogDebug(
                    "Critical field validated OK: TikCounter={TikCounter}, Field={FieldName}, ColumnType={ColumnType}, Value='{Value}'",
                    c.TikCounter,
                    criticalColumn.FieldName,
                    actualColumnType,
                    fieldValue);
            }
        }

        private string? GetColumnIdForField(string fieldName)
        {
            return fieldName switch
            {
                "DocumentType" => _mondaySettings.DocumentTypeStatusColumnId,
                "PlaintiffSide" => "color_mkxh8gsq", // צד תובע
                "DefendantSide" => "color_mkxh5x31", // צד נתבע
                _ => null
            };
        }

        private class CriticalColumnDefinition
        {
            public string FieldName { get; set; } = string.Empty;
            // ColumnType is no longer used - detected dynamically from Monday metadata
            public Func<OdcanitCase, string?> GetValue { get; set; } = _ => null;
            public string ValidationMessage { get; set; } = string.Empty;
        }
        
        /// <summary>
        /// Computes a deterministic SHA-256 hash of the hearing-relevant fields on an OdcanitCase.
        /// Used to detect hearing-only changes that should trigger a Monday update
        /// even when the main case data (tsModifyDate / OdcanitVersion) hasn't changed.
        /// Fields: HearingDate, HearingTime, HearingJudgeName, EffectiveCourtCity (HearingCity->HearingCourtName), MeetStatus.
        /// </summary>
        private static string ComputeHearingChecksum(OdcanitCase c)
        {
            var date = c.HearingDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "";
            var hour = c.HearingTime.HasValue
                ? $"{c.HearingTime.Value.Hours:D2}:{c.HearingTime.Value.Minutes:D2}"
                : "";
            var judge = (c.HearingJudgeName ?? "").Trim();
            var effectiveCity = (!string.IsNullOrWhiteSpace(c.HearingCity) ? c.HearingCity
                : c.HearingCourtName ?? "").Trim();
            var meetStatus = c.MeetStatus?.ToString(CultureInfo.InvariantCulture) ?? "";

            var input = $"{date}|{hour}|{judge}|{effectiveCity}|{meetStatus}";
            var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(input));
            return Convert.ToHexString(bytes).ToLowerInvariant();
        }

        /// <summary>
        /// Checks if a case belongs to Client 6 based on ClientVisualID.
        /// Client 6 has special handling: DocumentType is omitted from Monday sync.
        /// </summary>
        private static bool IsClient6(string? clientVisualID)
        {
            if (string.IsNullOrWhiteSpace(clientVisualID))
            {
                return false;
            }
            
            // Extract ClientNumber (substring before '\' if present, otherwise entire string)
            var backslashIndex = clientVisualID.IndexOf('\\');
            string clientNumberStr;
            
            if (backslashIndex > 0)
            {
                clientNumberStr = clientVisualID.Substring(0, backslashIndex).Trim();
            }
            else
            {
                clientNumberStr = clientVisualID.Trim();
            }
            
            return clientNumberStr == "6";
        }

        // ══════════════════════════════════════════════════════════════════
        // Retry, Dead-Letter, Run-Lock helpers
        // ══════════════════════════════════════════════════════════════════

        /// <summary>
        /// Executes an async operation with exponential backoff retry for transient failures.
        /// Non-transient exceptions (validation, permanent API errors) are re-thrown immediately.
        /// Returns (result, retryCount).
        /// </summary>
        private async Task<(T Result, int RetryCount)> ExecuteWithRetryAsync<T>(
            Func<Task<T>> operation,
            string operationName,
            int tikCounter,
            CancellationToken ct)
        {
            int retryCount = 0;
            for (int attempt = 0; attempt <= MaxRetryAttempts; attempt++)
            {
                try
                {
                    var result = await operation();
                    return (result, retryCount);
                }
                catch (CriticalFieldValidationException) { throw; }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) when (attempt < MaxRetryAttempts && IsTransientError(ex))
                {
                    retryCount = attempt + 1;
                    var delay = RetryDelays[Math.Min(attempt, RetryDelays.Length - 1)];
                    _logger.LogWarning(
                        "Transient error in {Operation} for TikCounter={TikCounter} (attempt {Attempt}/{MaxAttempts}). Retrying in {DelayMs}ms. Error: {Error}",
                        operationName, tikCounter, attempt + 1, MaxRetryAttempts, delay.TotalMilliseconds, ex.Message);
                    await Task.Delay(delay, ct);
                }
            }
            // Final attempt — let exceptions propagate
            var finalResult = await operation();
            return (finalResult, retryCount);
        }

        /// <summary>
        /// Determines whether an exception represents a transient failure worth retrying.
        /// Transient: network errors, HTTP 429/5xx, SQL deadlocks/timeouts.
        /// </summary>
        private static bool IsTransientError(Exception ex)
        {
            if (ex is HttpRequestException) return true;
            if (ex is TaskCanceledException tce && tce.InnerException is TimeoutException) return true;

            // Monday API: rate limit, server errors, complexity budget
            if (ex is Monday.MondayApiException mondayEx)
            {
                var msg = mondayEx.Message ?? "";
                var raw = mondayEx.RawErrorJson ?? "";
                if (msg.Contains("429", StringComparison.OrdinalIgnoreCase)
                    || msg.Contains("rate limit", StringComparison.OrdinalIgnoreCase)
                    || raw.Contains("RATE_LIMIT", StringComparison.OrdinalIgnoreCase)
                    || raw.Contains("COMPLEXITY_BUDGET_EXHAUSTED", StringComparison.OrdinalIgnoreCase)
                    || mondayEx.InnerException is HttpRequestException)
                    return true;
            }

            // SQL Server transient errors: deadlock (1205), timeout (-2)
            if (ex is Microsoft.Data.SqlClient.SqlException sqlEx)
            {
                return sqlEx.Number == 1205 || sqlEx.Number == -2;
            }

            return false;
        }

        /// <summary>
        /// Persists a failed case to the SyncFailures table for later inspection and reprocessing.
        /// </summary>
        private async Task PersistSyncFailureAsync(
            string runId, int tikCounter, string? tikNumber, long boardId,
            string operation, Exception ex, int retryAttempts, CancellationToken ct)
        {
            try
            {
                _integrationDb.SyncFailures.Add(new SyncFailure
                {
                    RunId = runId,
                    TikCounter = tikCounter,
                    TikNumber = tikNumber,
                    BoardId = boardId,
                    Operation = operation,
                    ErrorType = ex.GetType().Name,
                    ErrorMessage = Truncate(ex.Message, 2000),
                    StackTrace = Truncate(ex.StackTrace, 4000),
                    OccurredAtUtc = DateTime.UtcNow,
                    RetryAttempts = retryAttempts,
                    Resolved = false
                });
                await _integrationDb.SaveChangesAsync(ct);
            }
            catch (Exception persistEx)
            {
                _logger.LogWarning(persistEx,
                    "Failed to persist SyncFailure for TikCounter={TikCounter}: {Error}",
                    tikCounter, persistEx.Message);
            }
        }

        private static string? Truncate(string? value, int maxLength)
        {
            if (value == null) return null;
            return value.Length <= maxLength ? value : value[..maxLength];
        }

        // ── Run-lock ──

        /// <summary>
        /// Attempts to acquire a DB-based run lock. Returns true if lock was acquired.
        /// Expired locks (from crashed runs) are automatically reclaimed.
        /// </summary>
        private async Task<bool> TryAcquireRunLockAsync(string runId, CancellationToken ct)
        {
            try
            {
                var lockRow = await _integrationDb.SyncRunLocks.FirstOrDefaultAsync(x => x.Id == 1, ct);
                if (lockRow == null)
                {
                    lockRow = new SyncRunLock { Id = 1 };
                    _integrationDb.SyncRunLocks.Add(lockRow);
                }

                // If locked and not expired, another run is active
                if (lockRow.LockedByRunId != null && lockRow.ExpiresAtUtc.HasValue && lockRow.ExpiresAtUtc.Value > DateTime.UtcNow)
                {
                    _logger.LogWarning(
                        "Run-lock held by RunId={HeldByRunId}, expires at {ExpiresAtUtc}. Current RunId={CurrentRunId}",
                        lockRow.LockedByRunId, lockRow.ExpiresAtUtc, runId);
                    return false;
                }

                // Acquire (or reclaim expired lock)
                if (lockRow.LockedByRunId != null && lockRow.ExpiresAtUtc.HasValue && lockRow.ExpiresAtUtc.Value <= DateTime.UtcNow)
                {
                    _logger.LogWarning("Reclaiming expired run-lock from RunId={OldRunId}", lockRow.LockedByRunId);
                }

                lockRow.LockedByRunId = runId;
                lockRow.LockedAtUtc = DateTime.UtcNow;
                lockRow.ExpiresAtUtc = DateTime.UtcNow.Add(RunLockDuration);
                await _integrationDb.SaveChangesAsync(ct);
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to acquire run-lock, proceeding without lock. RunId={RunId}", runId);
                return true; // Fail-open: don't block the run if lock table is unavailable
            }
        }

        /// <summary>
        /// Releases the run lock after a sync run completes.
        /// </summary>
        private async Task ReleaseRunLockAsync(string runId, CancellationToken ct)
        {
            try
            {
                var lockRow = await _integrationDb.SyncRunLocks.FirstOrDefaultAsync(x => x.Id == 1, ct);
                if (lockRow != null && lockRow.LockedByRunId == runId)
                {
                    lockRow.LockedByRunId = null;
                    lockRow.LockedAtUtc = null;
                    lockRow.ExpiresAtUtc = null;
                    await _integrationDb.SaveChangesAsync(ct);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to release run-lock for RunId={RunId}", runId);
            }
        }
    }
}

