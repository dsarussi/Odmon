using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Globalization;
using Odmon.Worker.OdcanitAccess;
using Odmon.Worker.Data;
using Odmon.Worker.Monday;
using Odmon.Worker.Models;
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
        private readonly IOdcanitReader _odcanitReader;
        private readonly IntegrationDbContext _integrationDb;
        private readonly IMondayClient _mondayClient;
        private readonly IMondayMetadataProvider _mondayMetadataProvider;
        private readonly IConfiguration _config;
        private readonly ILogger<SyncService> _logger;
        private readonly ITestSafetyPolicy _safetyPolicy;
        private readonly MondaySettings _mondaySettings;

        public SyncService(
            IOdcanitReader odcanitReader,
            IntegrationDbContext integrationDb,
            IMondayClient mondayClient,
            IMondayMetadataProvider mondayMetadataProvider,
            IConfiguration config,
            ILogger<SyncService> logger,
            ITestSafetyPolicy safetyPolicy,
            IOptions<MondaySettings> mondayOptions)
        {
            _odcanitReader = odcanitReader;
            _integrationDb = integrationDb;
            _mondayClient = mondayClient;
            _mondayMetadataProvider = mondayMetadataProvider;
            _config = config;
            _logger = logger;
            _safetyPolicy = safetyPolicy;
            _mondaySettings = mondayOptions.Value ?? new MondaySettings();
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
            var useTodayOnly = _config.GetValue<bool>("Sync:UseTodayOnly", true);

            var safetySection = _config.GetSection("Safety");
            var testMode = safetySection.GetValue<bool>("TestMode", false);
            var testBoardId = safetySection.GetValue<long>("TestBoardId", 0);
            var testGroupId = _mondaySettings.TestGroupId;

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

            // Load cases created today (or all cases if UseTodayOnly is false)
            var today = DateTime.Today;
            var tomorrow = today.AddDays(1);

            List<OdcanitCase> newOrUpdatedCases;
            if (useTodayOnly)
            {
                newOrUpdatedCases = await _odcanitReader.GetCasesCreatedOnDateAsync(today, ct);
                _logger.LogInformation("Loaded cases created today from Odcanit: {Count}", newOrUpdatedCases.Count);
            }
            else
            {
                // Fallback: load all cases (for future use if needed)
                _logger.LogWarning("UseTodayOnly is disabled - loading all cases. This is not recommended for production.");
                newOrUpdatedCases = await _odcanitReader.GetCasesCreatedOnDateAsync(DateTime.MinValue, ct);
                _logger.LogInformation("Loaded all cases from Odcanit: {Count}", newOrUpdatedCases.Count);
            }

            var batch = (maxItems > 0 ? newOrUpdatedCases.Take(maxItems) : newOrUpdatedCases).ToList();
            var processed = new List<object>();
            int created = 0, updated = 0;
            int skippedNonTest = 0, skippedExistingNonTest = 0, skippedNoChange = 0, skippedNonDemo = 0;
            int failed = 0;

            foreach (var c in batch)
            {
                var caseBoardId = boardIdToUse;
                var caseGroupId = groupIdToUse;
                // DEMO: determine if the current case belongs to today's demo window.
                bool isInDemoWindow = c.tsCreateDate >= today && c.tsCreateDate < tomorrow;
                if (isInDemoWindow)
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
                bool shouldEnforceSafety = !isInDemoWindow && !isSafeTestCase;

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
                                await UpdateMondayItemAsync(mapping!, c, caseBoardId, itemName, syncAction.RequiresNameUpdate, syncAction.RequiresDataUpdate, testMode, ct);
                                updated++;
                                _logger.LogInformation(
                                    "Successfully updated Monday item: TikNumber={TikNumber}, TikCounter={TikCounter}, MondayItemId={MondayItemId}",
                                    c.TikNumber, c.TikCounter, mapping.MondayItemId);
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
            TryAddDropdownColumn(columnValues, _mondaySettings.ClientNumberColumnId, c.ClientVisualID);
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
            TryAddDateColumn(columnValues, _mondaySettings.HearingDateColumnId, c.HearingDate);
            TryAddHourColumn(columnValues, _mondaySettings.HearingHourColumnId, c.HearingTime);

            TryAddDecimalColumn(columnValues, _mondaySettings.RequestedClaimAmountColumnId, c.RequestedClaimAmount);
            TryAddDecimalColumn(columnValues, _mondaySettings.ProvenClaimAmountColumnId, c.ProvenClaimAmount);
            TryAddDecimalColumn(columnValues, _mondaySettings.JudgmentAmountColumnId, c.JudgmentAmount);
            TryAddStringColumn(columnValues, _mondaySettings.NotesColumnId, c.Notes);
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
            if (string.IsNullOrWhiteSpace(phoneNumber))
            {
                return null;
            }

            var digits = new string(phoneNumber.Where(char.IsDigit).ToArray());
            if (string.IsNullOrEmpty(digits))
            {
                return null;
            }

            string normalized;
            if (digits.StartsWith("0") && digits.Length > 1)
            {
                normalized = "+972" + digits.Substring(1);
            }
            else if (digits.StartsWith("972"))
            {
                normalized = "+" + digits;
            }
            else
            {
                normalized = "+" + digits;
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

        private static void TryAddHourColumn(Dictionary<string, object> columnValues, string? columnId, TimeSpan? value)
        {
            if (string.IsNullOrWhiteSpace(columnId) || value is null)
            {
                return;
            }

            columnValues[columnId] = value.Value.ToString(@"hh\:mm", CultureInfo.InvariantCulture);
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
            MondayItemMapping? mapping = null;
            string lookupMethod = "none";

            // Priority 1: Find mapping by TikNumber + BoardId (preferred method)
            if (!string.IsNullOrWhiteSpace(c.TikNumber))
            {
                mapping = await _integrationDb.MondayItemMappings
                    .FirstOrDefaultAsync(m => m.TikNumber == c.TikNumber && m.BoardId == boardId, ct);
                
                if (mapping != null)
                {
                    lookupMethod = "mapping_by_tiknumber_boardid";
                    _logger.LogDebug(
                        "Found mapping by TikNumber+BoardId: TikNumber={TikNumber}, BoardId={BoardId}, MondayItemId={MondayItemId}, TikCounter={TikCounter}",
                        c.TikNumber, boardId, mapping.MondayItemId, mapping.TikCounter);
                }
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
                            OdcanitVersion = c.tsModifyDate.ToString("o"),
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

            var odcanitVersion = c.tsModifyDate.ToString("o");
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
            var columnValuesJson = await BuildColumnValuesJsonAsync(boardId, c, forceNotStartedStatus: true, ct);
            var mondayItemId = await _mondayClient.CreateItemAsync(boardId, groupId, itemName, columnValuesJson, ct);

            var newMapping = new MondayItemMapping
            {
                TikCounter = c.TikCounter,
                TikNumber = c.TikNumber,
                BoardId = boardId,
                MondayItemId = mondayItemId,
                LastSyncFromOdcanitUtc = DateTime.UtcNow,
                OdcanitVersion = c.tsModifyDate.ToString("o"),
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
                var columnValuesJson = await BuildColumnValuesJsonAsync(boardId, c, forceNotStartedStatus: false, ct);
                await _mondayClient.UpdateItemAsync(boardId, mapping.MondayItemId, columnValuesJson, ct);
                mapping.OdcanitVersion = c.tsModifyDate.ToString("o");
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

