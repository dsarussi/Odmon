using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Globalization;
using Odmon.Worker.OdcanitAccess;
using Odmon.Worker.Data;
using Odmon.Worker.Monday;
using Odmon.Worker.Models;
using Odmon.Worker.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Odmon.Worker.Services
{
    public class SyncService
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
        private readonly ConcurrentDictionary<long, ColumnCacheEntry> _columnIdCache = new();

        public SyncService(
            ICaseSource caseSource,
            IntegrationDbContext integrationDb,
            IMondayClient mondayClient,
            IMondayMetadataProvider mondayMetadataProvider,
            IConfiguration config,
            ILogger<SyncService> logger,
            ITestSafetyPolicy safetyPolicy,
            IOptions<MondaySettings> mondayOptions,
            ISecretProvider secretProvider,
            HearingApprovalSyncService hearingApprovalSyncService,
            HearingNearestSyncService hearingNearestSyncService,
            ISkipLogger skipLogger)
        {
            _caseSource = caseSource;
            _integrationDb = integrationDb;
            _mondayClient = mondayClient;
            _mondayMetadataProvider = mondayMetadataProvider;
            _config = config;
            _logger = logger;
            _safetyPolicy = safetyPolicy;
            _mondaySettings = mondayOptions.Value ?? new MondaySettings();
            _secretProvider = secretProvider;
            _hearingApprovalSyncService = hearingApprovalSyncService;
            _hearingNearestSyncService = hearingNearestSyncService;
            _skipLogger = skipLogger;
        }

        public async Task SyncOdcanitToMondayAsync(CancellationToken ct)
        {
            var runId = Guid.NewGuid().ToString("N");
            var runStartedAtUtc = DateTime.UtcNow;

            var enabled = _config.GetValue<bool>("Sync:Enabled", true);
            if (!enabled)
            {
                _logger.LogInformation("Sync is disabled via configuration.");
                return;
            }

            var dryRun = _config.GetValue<bool>("Sync:DryRun", false);
            var maxItems = _config.GetValue<int>("Sync:MaxItemsPerRun", 50);

            var safetySection = _config.GetSection("Safety");
            var testMode = safetySection.GetValue<bool>("TestMode", false);
            var testBoardId = safetySection.GetValue<long>("TestBoardId", 0);
            var testGroupId = _mondaySettings.TestGroupId;

            var casesBoardId = _mondaySettings.CasesBoardId;
            if (casesBoardId == 0)
            {
                throw new InvalidOperationException(
                    "Monday:CasesBoardId is 0 or missing. " +
                    "Set configuration key 'Monday:CasesBoardId' (or environment variable 'Monday__CasesBoardId') to a valid Monday.com board ID.");
            }

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

            // NEW RULE: Fetch data ONLY by TikCounter - NO date filters
            int[] tikCounters;
            var testingSection = _config.GetSection("Testing");
            var testingEnabled = testingSection.GetValue<bool>("Enable", false);
            if (testingEnabled)
            {
                var testingSource = testingSection.GetValue<string>("Source") ?? "IntegrationDbTestCases1808";
                var testingTikCounters = testingSection.GetSection("TikCounters").Get<int[]>() ?? Array.Empty<int>();

                if (testingTikCounters.Length == 0)
                {
                    _logger.LogError("Testing:Enable=true but Testing:TikCounters is missing or empty. No data will be processed.");
                    return;
                }

                tikCounters = testingTikCounters;
                _logger.LogInformation(
                    "TEST MODE ENABLED - source={Source} - tikCounters={TikCounters}",
                    testingSource,
                    string.Join(", ", tikCounters));
            }
            else
            {
                var tikCounterSection = _config.GetSection("Sync:TikCounters");
                tikCounters = tikCounterSection.Get<int[]>() ?? Array.Empty<int>();

                if (tikCounters.Length == 0)
                {
                    _logger.LogError("Sync:TikCounters configuration is missing or empty. Worker must have explicit TikCounter list to fetch data. No data will be processed.");
                    return;
                }

                _logger.LogInformation("Fetching cases by TikCounter only (ignoring all date filters): {TikCounters}", string.Join(", ", tikCounters));
            }

            // In test mode _caseSource is IntegrationTestCaseSource (no IOdcanitReader); GuardOdcanitReader is never used for case load.
            List<OdcanitCase> newOrUpdatedCases = await _caseSource.GetCasesByTikCountersAsync(tikCounters, ct);
            _logger.LogInformation("Loaded {Count} cases by TikCounter from case source", newOrUpdatedCases.Count);

            var batch = (maxItems > 0 ? newOrUpdatedCases.Take(maxItems) : newOrUpdatedCases).ToList();
            var processed = new List<object>();
            int created = 0, updated = 0;
            int skippedNonTest = 0, skippedExistingNonTest = 0, skippedNoChange = 0, skippedNonDemo = 0;
            int failed = 0;

            foreach (var c in batch)
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
                                mondayIdForLog = await CreateMondayItemAsync(c, caseBoardId, caseGroupId!, itemName, testMode, ct);
                                created++;
                                _logger.LogInformation(
                                    "Successfully created Monday item: TikNumber={TikNumber}, TikCounter={TikCounter}, MondayItemId={MondayItemId}",
                                    c.TikNumber, c.TikCounter, mondayIdForLog);
                            }
                            catch (Monday.MondayApiException mondayEx)
                            {
                                action = "failed_create";
                                failed++;
                                errorMessage = mondayEx.Message;
                                
                                // Log with full context from MondayApiException
                                _logger.LogError(mondayEx,
                                    "Monday API error during create_item: TikNumber={TikNumber}, TikCounter={TikCounter}, BoardId={BoardId}, Operation={Operation}, ItemId={ItemId}, Error={Error}, ColumnValuesSnippet={ColumnValuesSnippet}",
                                    c.TikNumber, c.TikCounter, caseBoardId, mondayEx.Operation ?? "create_item", mondayEx.ItemId, mondayEx.Message, mondayEx.ColumnValuesSnippet ?? "<none>");
                                
                                processed.Add(LogCase(action, c, itemName, prefixApplied, testMode, dryRun, caseBoardId, mondayIdForLog, wasNoChange, errorMessage));
                                continue;
                            }
                            catch (Exception ex)
                            {
                                action = "failed_create";
                                failed++;
                                errorMessage = ex.Message;
                                
                                _logger.LogError(ex,
                                    "Unexpected error during create_item: TikNumber={TikNumber}, TikCounter={TikCounter}, BoardId={BoardId}, Error={Error}",
                                    c.TikNumber, c.TikCounter, caseBoardId, ex.Message);
                                
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
                            "Updating existing Monday item: TikNumber={TikNumber}, TikCounter={TikCounter}, MondayItemId={MondayItemId}, BoardId={BoardId}, RequiresDataUpdate={RequiresDataUpdate}, RequiresNameUpdate={RequiresNameUpdate}",
                            c.TikNumber, c.TikCounter, mapping!.MondayItemId, caseBoardId, syncAction.RequiresDataUpdate, syncAction.RequiresNameUpdate);
                        if (!dryRun)
                        {
                            try
                            {
                                var itemState = await _mondayClient.GetItemStateAsync(caseBoardId, mapping.MondayItemId, ct);
                                if (itemState != null && !string.Equals(itemState, "active", StringComparison.OrdinalIgnoreCase))
                                {
                                    var oldItemId = mapping.MondayItemId;
                                    var columnValuesJson = await BuildColumnValuesJsonAsync(caseBoardId, c, forceNotStartedStatus: true, ct);
                                    var newItemId = await _mondayClient.CreateItemAsync(caseBoardId, caseGroupId!, itemName, columnValuesJson, ct);
                                    mapping.MondayItemId = newItemId;
                                    mapping.OdcanitVersion = c.tsModifyDate?.ToString("o") ?? string.Empty;
                                    mapping.MondayChecksum = itemName;
                                    mapping.LastSyncFromOdcanitUtc = DateTime.UtcNow;
                                    mapping.IsTest = testMode;
                                    await _integrationDb.SaveChangesAsync(ct);
                                    _logger.LogWarning(
                                        "Monday item inactive (state={State}), created new item and updated mapping: TikCounter={TikCounter}, TikNumber={TikNumber}, oldItemId={OldItemId}, newItemId={NewItemId}",
                                        itemState, c.TikCounter, c.TikNumber ?? "<null>", oldItemId, newItemId);
                                    updated++;
                                }
                                else
                                {
                                    await UpdateMondayItemAsync(mapping!, c, caseBoardId, itemName, syncAction.RequiresNameUpdate, syncAction.RequiresDataUpdate, testMode, ct);
                                    updated++;
                                    _logger.LogInformation(
                                        "Successfully updated Monday item: TikNumber={TikNumber}, TikCounter={TikCounter}, MondayItemId={MondayItemId}",
                                        c.TikNumber, c.TikCounter, mapping.MondayItemId);
                                }
                            }
                            catch (Monday.MondayApiException mondayEx)
                            {
                                action = "failed_update";
                                failed++;
                                errorMessage = mondayEx.Message;
                                
                                // Log with full context from MondayApiException
                                _logger.LogError(mondayEx,
                                    "Monday API error during update: TikNumber={TikNumber}, TikCounter={TikCounter}, MondayItemId={MondayItemId}, BoardId={BoardId}, Operation={Operation}, Error={Error}, ColumnValuesSnippet={ColumnValuesSnippet}",
                                    c.TikNumber, c.TikCounter, mapping.MondayItemId, caseBoardId, mondayEx.Operation ?? "change_multiple_column_values", mondayEx.Message, mondayEx.ColumnValuesSnippet ?? "<none>");
                                
                                processed.Add(LogCase(action, c, itemName, prefixApplied, testMode, dryRun, caseBoardId, mondayIdForLog, wasNoChange, errorMessage));
                                continue;
                            }
                            catch (Exception ex)
                            {
                                action = "failed_update";
                                failed++;
                                errorMessage = ex.Message;
                                
                                _logger.LogError(ex,
                                    "Unexpected error during update: TikNumber={TikNumber}, TikCounter={TikCounter}, MondayItemId={MondayItemId}, BoardId={BoardId}, Error={Error}",
                                    c.TikNumber, c.TikCounter, mapping.MondayItemId, caseBoardId, ex.Message);
                                
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
            }

            var runSummary = new
            {
                RunId = runId,
                StartedAtUtc = runStartedAtUtc,
                FinishedAtUtc = DateTime.UtcNow,
                MaxItems = maxItems,
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
                Level = "Info",
                Message = $"Run {runId} summary: created={created}, updated={updated}, skipped_non_test={skippedNonTest}, skipped_existing_non_test_mapping={skippedExistingNonTest}, skipped_non_demo={skippedNonDemo}, skipped_no_change={skippedNoChange}, failed={failed}, batch={batch.Count}",
                Details = JsonSerializer.Serialize(runSummary)
            });

            await _integrationDb.SaveChangesAsync(ct);

            // Phase-2: hearing approval write-back runs even when main sync skips/no-change
            await _hearingApprovalSyncService.SyncAsync(batch, ct);

            // Nearest hearing sync: update Monday hearing date/judge/city/status from vwExportToOuterSystems_YomanData
            await _hearingNearestSyncService.SyncNearestHearingsAsync(boardIdToUse, ct);

            _logger.LogInformation(
                "Sync run {RunId} completed: created={Created}, updated={Updated}, skipped_non_test={SkippedNonTest}, skipped_existing_non_test_mapping={SkippedExistingNonTest}, skipped_non_demo={SkippedNonDemo}, skipped_no_change={SkippedNoChange}, failed={Failed}, total_processed={TotalProcessed}",
                runId,
                created,
                updated,
                skippedNonTest,
                skippedExistingNonTest,
                skippedNonDemo,
                skippedNoChange,
                failed,
                batch.Count);
            
            if (failed > 0)
            {
                _logger.LogWarning(
                    "Sync run {RunId} completed with {FailedCount} failure(s). Review error logs above for details.",
                    runId, failed);
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

            var hasHearingDate = c.HearingDate.HasValue;
            var hasJudge = !string.IsNullOrWhiteSpace(c.JudgeName);
            var hasCourtCity = !string.IsNullOrWhiteSpace(c.CourtCity);
            var canPublishHearing = hasHearingDate && hasJudge && hasCourtCity;

            _logger.LogDebug(
                "Hearing gating TikCounter {TikCounter}: Date={HasDate}, Judge={HasJudge}, City={HasCity}, CanPublish={CanPublish}",
                c.TikCounter,
                hasHearingDate,
                hasJudge,
                hasCourtCity,
                canPublishHearing);

            if (canPublishHearing)
            {
                TryAddDateColumn(columnValues, _mondaySettings.HearingDateColumnId, c.HearingDate);
                await TryAddHourColumnAsync(columnValues, boardId, _mondaySettings.HearingHourColumnId, c.HearingTime, c.TikCounter, ct);
            }
            else
            {
                if (hasHearingDate || hasJudge || hasCourtCity)
                {
                    _logger.LogWarning(
                        "Hearing update suppressed for TikCounter {TikCounter}: incomplete hearing data (Date={HasDate}, Judge={HasJudge}, City={HasCity})",
                        c.TikCounter,
                        hasHearingDate,
                        hasJudge,
                        hasCourtCity);
                }
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
            
            // Resolve policy holder name column ID dynamically by title
            var policyHolderNameColumnId = await _mondayMetadataProvider.GetColumnIdByTitleAsync(boardId, "שם בעל פוליסה", ct);
            TryAddStringColumn(columnValues, policyHolderNameColumnId, c.PolicyHolderName);
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
            TryAddStringColumn(columnValues, _mondaySettings.CourtCityColumnId, c.CourtCity);
            TryAddStringColumn(columnValues, _mondaySettings.CourtCaseNumberColumnId, c.CourtCaseNumber);
            TryAddStringColumn(columnValues, _mondaySettings.JudgeNameColumnId, c.JudgeName);
            TryAddStringColumn(columnValues, _mondaySettings.AttorneyNameColumnId, c.AttorneyName);
            TryAddStringColumn(columnValues, _mondaySettings.DefenseStreetColumnId, c.DefenseStreet);
            TryAddStringColumn(columnValues, _mondaySettings.ClaimStreetColumnId, c.ClaimStreet);
            TryAddStringColumn(columnValues, _mondaySettings.CaseFolderIdColumnId, c.CaseFolderId);
            TryAddStatusLabelColumn(columnValues, _mondaySettings.TaskTypeStatusColumnId, MapTaskTypeLabel(c.TikType));

            // Legal user data (UserData view vwExportToOuterSystems_UserData): צד תובע / צד נתבע -> Monday status columns
            TryAddStatusLabelColumn(columnValues, "color_mkxh8gsq", MapPlaintiffSideLabel(c.PlaintiffSideRaw));
            TryAddStatusLabelColumn(columnValues, "color_mkxh5x31", MapDefendantSideLabel(c.DefendantSideRaw));
            TryAddStringColumn(columnValues, _mondaySettings.ResponsibleTextColumnId, DetermineResponsibleText(c));

            // Set document type based on client number
            var documentType = DetermineDocumentType(c.ClientVisualID);
            if (!string.IsNullOrWhiteSpace(documentType))
            {
                TryAddStatusLabelColumn(columnValues, _mondaySettings.DocumentTypeStatusColumnId, documentType);
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
                _logger.LogWarning(
                    ex,
                    "Failed to resolve allowed labels for dropdown column {ColumnId} on board {BoardId}. ClientNumber '{ClientNumber}' for TikCounter {TikCounter}, TikNumber {TikNumber} will not be sent.",
                    columnId,
                    boardId,
                    trimmedClientNumber,
                    tikCounter,
                    tikNumber ?? "<null>");
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

        private static string? DetermineDocumentType(string? clientVisualID)
        {
            if (string.IsNullOrWhiteSpace(clientVisualID))
            {
                return null;
            }

            // Try to parse as integer
            if (!int.TryParse(clientVisualID.Trim(), out var clientNumber))
            {
                return null;
            }

            // Client number == 1 → "כתב הגנה" (defense)
            if (clientNumber == 1)
            {
                return "כתב הגנה";
            }

            // Client number in {4, 7, 9} OR has 3+ digits → "כתב תביעה" (claim)
            if (clientNumber == 4 || clientNumber == 7 || clientNumber == 9 || clientNumber >= 100)
            {
                return "כתב תביעה";
            }

            // For all other client numbers, leave empty
            return null;
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
            if (boardId == 0)
            {
                throw new InvalidOperationException(
                    "BoardId is 0 - missing config key Monday:CasesBoardId (or Monday__CasesBoardId environment variable). " +
                    $"Cannot sync TikCounter={c.TikCounter}, TikNumber={c.TikNumber ?? "<null>"}.");
            }

            MondayItemMapping? mapping = null;
            string lookupMethod = "none";

            // Priority 1: Find mapping by TikCounter + BoardId (source of truth)
            mapping = await _integrationDb.MondayItemMappings
                .AsNoTracking()
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
                    .AsNoTracking()
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

            if (!requiresDataUpdate && !requiresNameUpdate)
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
                "DetermineSyncAction: Changes detected. TikNumber={TikNumber}, TikCounter={TikCounter}, MondayItemId={MondayItemId}, RequiresDataUpdate={RequiresDataUpdate}, RequiresNameUpdate={RequiresNameUpdate}. Action=update",
                c.TikNumber, c.TikCounter, mapping.MondayItemId, requiresDataUpdate, requiresNameUpdate);

            return new SyncAction
            {
                Action = "update",
                RequiresDataUpdate = requiresDataUpdate,
                RequiresNameUpdate = requiresNameUpdate
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
            bool testMode,
            CancellationToken ct)
        {
            // Update item name if it has changed
            if (requiresNameUpdate)
            {
                await _mondayClient.UpdateItemNameAsync(boardId, mapping.MondayItemId, itemName, ct);
            }

            // Update column values if data has changed
            if (requiresDataUpdate)
            {
                _logger.LogDebug(
                    "Court mapping for TikCounter {TikCounter}, TikNumber {TikNumber}: CourtCaseNumber -> {CourtCaseNumber}, CourtCity -> {CourtCity}",
                    c.TikCounter,
                    c.TikNumber ?? "<null>",
                    c.CourtCaseNumber ?? "<null>",
                    c.CourtCity ?? "<null>");

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
            mapping.IsTest = testMode;
        }

        private class SyncAction
        {
            public string Action { get; set; } = string.Empty; // "create", "update", or "skip"
            public bool RequiresNameUpdate { get; set; }
            public bool RequiresDataUpdate { get; set; }
        }
    }
}

