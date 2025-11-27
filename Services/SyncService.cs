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
        private readonly IConfiguration _config;
        private readonly ILogger<SyncService> _logger;
        private readonly ITestSafetyPolicy _safetyPolicy;
        private readonly MondaySettings _mondaySettings;

        public SyncService(
            IOdcanitReader odcanitReader,
            IntegrationDbContext integrationDb,
            IMondayClient mondayClient,
            IConfiguration config,
            ILogger<SyncService> logger,
            ITestSafetyPolicy safetyPolicy,
            IOptions<MondaySettings> mondayOptions)
        {
            _odcanitReader = odcanitReader;
            _integrationDb = integrationDb;
            _mondayClient = mondayClient;
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

            // DEMO: load cases created today directly from Odcanit.
            var today = DateTime.Today;
            var tomorrow = today.AddDays(1);

            var newOrUpdatedCases = await _odcanitReader.GetCasesCreatedOnDateAsync(today, ct);

            _logger.LogInformation("DEMO: total cases from Odcanit for today: {Count}", newOrUpdatedCases.Count);

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
                string action;
                bool wasNoChange = false;
                string? errorMessage = null;

                // Explicit override for the single test case (TikCounter == 31490)
                bool isExplicitTestCase = c.TikCounter == 31490;
                bool isSafeTestCase = _safetyPolicy.IsTestCase(c) || isExplicitTestCase;
                bool shouldEnforceSafety = !isInDemoWindow && !isSafeTestCase;

                var mapping = await _integrationDb.MondayItemMappings
                    .FirstOrDefaultAsync(m => m.TikCounter == c.TikCounter, ct);

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

                var odcanitVersion = c.tsModifyDate.ToString("o");

                if (mapping == null)
                {
                    var columnValuesJson = BuildColumnValuesJson(c, forceNotStartedStatus: true);
                    action = dryRun ? "dry-create" : "created";
                    if (!dryRun)
                    {
                        try
                        {
                            mondayIdForLog = await _mondayClient.CreateItemAsync(caseBoardId, caseGroupId!, itemName, columnValuesJson, ct);

                            var newMapping = new MondayItemMapping
                            {
                                TikCounter = c.TikCounter,
                                MondayItemId = mondayIdForLog,
                                LastSyncFromOdcanitUtc = DateTime.UtcNow,
                                OdcanitVersion = odcanitVersion,
                                MondayChecksum = itemName,
                                IsTest = testMode
                            };
                            _integrationDb.MondayItemMappings.Add(newMapping);
                        }
                        catch (Exception ex)
                        {
                            action = "failed_create";
                            failed++;
                            errorMessage = ex.Message;
                            processed.Add(LogCase(action, c, itemName, prefixApplied, testMode, dryRun, caseBoardId, mondayIdForLog, wasNoChange, errorMessage));
                            continue;
                        }
                    }
                    created++;
                }
                else
                {
                    var requiresUpdate = mapping.OdcanitVersion != odcanitVersion;
                    var columnValuesJson = BuildColumnValuesJson(c);
                    var requiresNameUpdate = mapping.MondayChecksum != itemName;
                    mondayIdForLog = mapping.MondayItemId;

                    if (!requiresUpdate && !requiresNameUpdate)
                    {
                        action = "skipped_no_change";
                        wasNoChange = true;
                        skippedNoChange++;
                    }
                    else
                    {
                        action = dryRun ? "dry-update" : "updated";
                        if (!dryRun)
                        {
                            try
                            {
                                // Update item name if it has changed
                                if (requiresNameUpdate)
                                {
                                    await _mondayClient.UpdateItemNameAsync(caseBoardId, mapping.MondayItemId, itemName, ct);
                                }

                                // Update column values if data has changed
                                if (requiresUpdate)
                                {
                                    await _mondayClient.UpdateItemAsync(caseBoardId, mapping.MondayItemId, columnValuesJson, ct);
                                    mapping.OdcanitVersion = odcanitVersion;
                                }
                            }
                            catch (Exception ex)
                            {
                                action = "failed_update";
                                failed++;
                                errorMessage = ex.Message;
                                processed.Add(LogCase(action, c, itemName, prefixApplied, testMode, dryRun, caseBoardId, mondayIdForLog, wasNoChange, errorMessage));
                                continue;
                            }
                        }

                        if (!dryRun)
                        {
                            mapping.LastSyncFromOdcanitUtc = DateTime.UtcNow;
                            mapping.MondayChecksum = itemName;
                            mapping.IsTest = testMode;
                        }

                        updated++;
                    }
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
                "Run {RunId}: created={Created}, updated={Updated}, skipped_non_test={SkippedNonTest}, skipped_existing_non_test_mapping={SkippedExistingNonTest}, skipped_non_demo={SkippedNonDemo}, skipped_no_change={SkippedNoChange}, failed={Failed}, batch={Batch}",
                runId,
                created,
                updated,
                skippedNonTest,
                skippedExistingNonTest,
                skippedNonDemo,
                skippedNoChange,
                failed,
                batch.Count);
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

        private string BuildColumnValuesJson(OdcanitCase c, bool forceNotStartedStatus = false)
        {
            var columnValues = new Dictionary<string, object>();

            TryAddStringColumn(columnValues, _mondaySettings.CaseNumberColumnId, c.TikNumber);
            TryAddDropdownColumn(columnValues, _mondaySettings.ClientNumberColumnId, c.ClientVisualID);
            TryAddStringColumn(columnValues, _mondaySettings.ClaimNumberColumnId, c.HozlapTikNumber);

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
            TryAddStringColumn(columnValues, _mondaySettings.CourtCityColumnId, c.CourtCity);
            TryAddStringColumn(columnValues, _mondaySettings.CourtCaseNumberColumnId, c.CourtCaseNumber);
            TryAddStringColumn(columnValues, _mondaySettings.JudgeNameColumnId, c.JudgeName);
            TryAddStringColumn(columnValues, _mondaySettings.AttorneyNameColumnId, c.AttorneyName);
            TryAddStringColumn(columnValues, _mondaySettings.DefenseStreetColumnId, c.DefenseStreet);
            TryAddStringColumn(columnValues, _mondaySettings.ClaimStreetColumnId, c.ClaimStreet);
            TryAddStringColumn(columnValues, _mondaySettings.CaseFolderIdColumnId, c.CaseFolderId);
            TryAddStatusLabelColumn(columnValues, _mondaySettings.TaskTypeStatusColumnId, MapTaskTypeLabel(c.TikType));
            TryAddStringColumn(columnValues, _mondaySettings.ResponsibleTextColumnId, DetermineResponsibleText(c));

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
    }
}

