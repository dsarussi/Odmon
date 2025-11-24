using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
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
            var testGroupId = safetySection["TestGroupId"];

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
            int skippedNonTest = 0, skippedExistingNonTest = 0, skippedNoChange = 0;
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

                if (!isInDemoWindow && !isSafeTestCase)
                {
                    action = "skipped_non_test";
                    skippedNonTest++;

                    processed.Add(LogCase(action, c, itemName, prefixApplied, testMode, dryRun, caseBoardId, 0, wasNoChange, errorMessage));
                    continue;
                }

                var mapping = await _integrationDb.MondayItemMappings
                    .FirstOrDefaultAsync(m => m.TikCounter == c.TikCounter, ct);

                long mondayIdForLog = mapping?.MondayItemId ?? 0;

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
                                MondayChecksum = itemName
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
                SkippedNoChange = skippedNoChange,
                Failed = failed,
                Processed = processed
            };

            _integrationDb.SyncLogs.Add(new SyncLog
            {
                CreatedAtUtc = DateTime.UtcNow,
                Source = "SyncService",
                Level = "Info",
                Message = $"Run {runId} summary: created={created}, updated={updated}, skipped_non_test={skippedNonTest}, skipped_existing_non_test_mapping={skippedExistingNonTest}, skipped_no_change={skippedNoChange}, failed={failed}, batch={batch.Count}",
                Details = JsonSerializer.Serialize(runSummary)
            });

            await _integrationDb.SaveChangesAsync(ct);

            _logger.LogInformation(
                "Run {RunId}: created={Created}, updated={Updated}, skipped_non_test={SkippedNonTest}, skipped_existing_non_test_mapping={SkippedExistingNonTest}, skipped_no_change={SkippedNoChange}, failed={Failed}, batch={Batch}",
                runId,
                created,
                updated,
                skippedNonTest,
                skippedExistingNonTest,
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
            var tikNumber = (c.TikNumber ?? string.Empty).Trim();
            var baseName = tikNumber;

            if (!testMode)
            {
                return (baseName, false);
            }

            if (baseName.StartsWith("[TEST] ", StringComparison.Ordinal))
            {
                return (baseName, false);
            }

            return ($"[TEST] {baseName}", true);
        }

        private string BuildColumnValuesJson(OdcanitCase c, bool forceNotStartedStatus = false)
        {
            var columnValues = new Dictionary<string, object>();

            // Case internal number (TikNumber) → text_mkwe19hn ("מספר תיק")
            if (!string.IsNullOrWhiteSpace(c.TikNumber))
            {
                columnValues["text_mkwe19hn"] = c.TikNumber;
            }

            // Client number (ClientVisualID) → text_mkwjaxeh ("מספר לקוח")
            if (!string.IsNullOrWhiteSpace(c.ClientVisualID))
            {
                columnValues["text_mkwjaxeh"] = c.ClientVisualID;
            }

            // Claim/lawsuit number (HozlapTikNumber) → text_mkwjy5pg ("מספר תביעה")
            if (!string.IsNullOrWhiteSpace(c.HozlapTikNumber))
            {
                columnValues["text_mkwjy5pg"] = c.HozlapTikNumber;
            }

            var phoneColumnId = ResolveClientPhoneColumnId();
            if (!string.IsNullOrWhiteSpace(phoneColumnId))
            {
                if (!string.IsNullOrWhiteSpace(c.ClientPhone))
                {
                    columnValues[phoneColumnId] = new { phone = c.ClientPhone, countryShortName = "IL" };
                    _logger.LogDebug("Including phone for TikCounter {TikCounter} on column {ColumnId}", c.TikCounter, phoneColumnId);
                }
                else
                {
                    _logger.LogDebug("No phone available for TikCounter {TikCounter}; column {ColumnId} left empty", c.TikCounter, phoneColumnId);
                }
            }

            var emailColumnId = ResolveClientEmailColumnId();
            if (!string.IsNullOrWhiteSpace(emailColumnId))
            {
                if (!string.IsNullOrWhiteSpace(c.ClientEmail))
                {
                    columnValues[emailColumnId] = c.ClientEmail;
                    _logger.LogDebug("Including email for TikCounter {TikCounter} on column {ColumnId}", c.TikCounter, emailColumnId);
                }
                else
                {
                    _logger.LogDebug("No email available for TikCounter {TikCounter}; column {ColumnId} left empty", c.TikCounter, emailColumnId);
                }
            }

            // Case open date (tsCreateDate) → date4 ("תאריך פתיחת תיק")
            columnValues["date4"] = new { date = c.tsCreateDate.ToString("yyyy-MM-dd") };

            // Event date → date_mkwj3780 ("תאריך אירוע")
            if (c.EventDate.HasValue)
            {
                columnValues["date_mkwj3780"] = new { date = c.EventDate.Value.ToString("yyyy-MM-dd") };
            }

            // Claim amount → numeric_mkxw7s29 ("הסעד המבוקש ( סכום תביעה)")
            if (c.ClaimAmount.HasValue)
            {
                columnValues["numeric_mkxw7s29"] = c.ClaimAmount.Value.ToString();
            }

            // Notes → long_text_mkwe5h8v ("הערות")
            if (!string.IsNullOrWhiteSpace(c.Notes))
            {
                columnValues["long_text_mkwe5h8v"] = c.Notes;
            }

            // Status → color_mkwefnbx ("סטטוס תיק")
            if (forceNotStartedStatus)
            {
                // For new items, set to "חדש" (not started)
                // Using label "חדש" - if this doesn't work, we may need to use index instead
                columnValues["color_mkwefnbx"] = new { label = "חדש" };
            }
            else
            {
                // For updates, map the status from Odcanit
                var statusIndex = MapStatusIndex(c.StatusName);
                columnValues["color_mkwefnbx"] = new { index = statusIndex };
            }

            return JsonSerializer.Serialize(columnValues);
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

