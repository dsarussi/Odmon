using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Odmon.Worker.Configuration;
using Odmon.Worker.Data;
using Odmon.Worker.Models;

namespace Odmon.Worker.Services
{
    public class DocumentIngestionService
    {
        private readonly IntegrationDbContext _integrationDb;
        private readonly DocumentIngestionMondayService _mondayService;
        private readonly OdcanitDocumentWriter _documentWriter;
        private readonly NispahWriterService _nispahWriter;
        private readonly ICaseAnnexWriteStateRepository _caseAnnexStateRepo;
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly IEmailNotifier _emailNotifier;
        private readonly DocumentIngestionSettings _settings;
        private readonly ILogger<DocumentIngestionService> _logger;

        private int _totalProcessed;
        private int _totalSucceeded;
        private int _totalFailed;
        private int _totalSkipped;
        private int _accidentStoryEmptyCount;
        private int _duplicateSkipCount;

        public DocumentIngestionService(
            IntegrationDbContext integrationDb,
            DocumentIngestionMondayService mondayService,
            OdcanitDocumentWriter documentWriter,
            NispahWriterService nispahWriter,
            ICaseAnnexWriteStateRepository caseAnnexStateRepo,
            IHttpClientFactory httpClientFactory,
            IEmailNotifier emailNotifier,
            IOptions<DocumentIngestionSettings> settings,
            ILogger<DocumentIngestionService> logger)
        {
            _integrationDb = integrationDb;
            _mondayService = mondayService;
            _documentWriter = documentWriter;
            _nispahWriter = nispahWriter;
            _caseAnnexStateRepo = caseAnnexStateRepo;
            _httpClientFactory = httpClientFactory;
            _emailNotifier = emailNotifier;
            _settings = settings.Value;
            _logger = logger;
        }

        public async Task RunIngestionAsync(CancellationToken ct, string? runId = null)
        {
            _totalProcessed = 0;
            _totalSucceeded = 0;
            _totalFailed = 0;
            _totalSkipped = 0;
            _accidentStoryEmptyCount = 0;
            _duplicateSkipCount = 0;

            var enabledSources = new List<string> { $"Questionnaire({_settings.BoardId})" };
            if (_settings.TasksSource is { Enabled: true })
                enabledSources.Add($"Tasks({_settings.TasksSource.BoardId})");

            var sw = Stopwatch.StartNew();
            _logger.LogInformation("DOCINGESTION RUN START | Sources=[{Sources}]", string.Join(", ", enabledSources));

            await ProcessQuestionnaireSourceAsync(ct, runId);

            if (_settings.TasksSource is { Enabled: true })
                await ProcessTasksSourceAsync(ct);

            sw.Stop();
            if (_accidentStoryEmptyCount > 0)
                _logger.LogInformation("ACCIDENTSTORY EMPTY SUMMARY | Count={Count}", _accidentStoryEmptyCount);
            if (_duplicateSkipCount > 0)
                _logger.LogInformation("DOCINGESTION DUPLICATE SUMMARY | Count={Count}", _duplicateSkipCount);
            _logger.LogInformation(
                "DOCINGESTION RUN COMPLETE | Sources=[{Sources}], Elapsed={ElapsedMs}ms, Processed={Processed}, Succeeded={Succeeded}, Failed={Failed}, Skipped={Skipped}",
                string.Join(", ", enabledSources), sw.ElapsedMilliseconds, _totalProcessed, _totalSucceeded, _totalFailed, _totalSkipped);
        }

        private async Task ProcessQuestionnaireSourceAsync(CancellationToken ct, string? runId)
        {
            var boardId = _settings.BoardId;
            _logger.LogInformation("DOCINGESTION SOURCE START | Source=Questionnaire, BoardId={BoardId}", boardId);
            var sourceSw = Stopwatch.StartNew();

            List<DocumentIngestionMondayService.QuestionnaireItem> items;
            try
            {
                items = await _mondayService.FetchQuestionnaireItemsAsync(
                    boardId, _settings.Columns, _settings.RelationColumnId,
                    _settings.ItemsPageLimit, ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "DOCINGESTION failed to fetch questionnaire items from board {BoardId}", boardId);
                SendAlert("Failed to fetch questionnaire board", null, ex);
                return;
            }

            _logger.LogInformation("DOCINGESTION | Processing {ItemCount} questionnaire items, DelayMs={DelayMs}",
                items.Count, _settings.ItemProcessingDelayMs);

            foreach (var item in items)
            {
                if (ct.IsCancellationRequested) break;

                try
                {
                    await ProcessQuestionnaireItemAsync(item, ct, runId);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "DOCINGESTION unhandled error processing questionnaire item {ItemId}", item.ItemId);
                }

                if (_settings.ItemProcessingDelayMs > 0)
                    await Task.Delay(_settings.ItemProcessingDelayMs, ct);
            }

            sourceSw.Stop();
            _logger.LogInformation("DOCINGESTION SOURCE COMPLETE | Source=Questionnaire, BoardId={BoardId}, Items={ItemCount}, Elapsed={ElapsedMs}ms",
                boardId, items.Count, sourceSw.ElapsedMilliseconds);
        }

        private async Task ProcessTasksSourceAsync(CancellationToken ct)
        {
            var ts = _settings.TasksSource!;
            _logger.LogInformation("DOCINGESTION SOURCE START | Source=Tasks, BoardId={BoardId}, TestTikNumber={TestTikNumber}",
                ts.BoardId, ts.TestTikNumber ?? "<none>");
            var sourceSw = Stopwatch.StartNew();
            int tasksFetched = 0, tasksProcessed = 0;
            var prevSucceeded = _totalSucceeded;
            var prevFailed = _totalFailed;
            var prevSkipped = _totalSkipped;

            try
            {
                var taskItems = await _mondayService.FetchTaskItemsAsync(
                    ts.BoardId, ts.TaskStatusColumnId, ts.FileColumnId,
                    ts.TikNumberColumnId,
                    ts.IsTestMode ? ts.TestModePageLimit : ts.ItemsPageLimit,
                    ts.IsTestMode,
                    ts.FetchTimeoutSeconds,
                    ct);

                tasksFetched = taskItems.Count;
                _logger.LogInformation("TASKDOC FETCH SUMMARY | TotalFetched={Count}", tasksFetched);

                if (ts.IsTestMode && tasksFetched > 0)
                {
                    int matchedItems = 0, successStatusItems = 0, itemsWithFile = 0, readyToImport = 0;
                    foreach (var ti in taskItems)
                    {
                        var lookupTik = ti.TikNumber?.Trim() ?? "";
                        var sMatch = string.Equals(ti.StatusLabel?.Trim(), ts.SuccessStatusLabel, StringComparison.Ordinal);
                        var hFile = ti.FileAssets.Count > 0;
                        var mTest = string.Equals(lookupTik, ts.TestTikNumber!.Trim(), StringComparison.OrdinalIgnoreCase);
                        if (mTest) matchedItems++;
                        if (mTest && sMatch) successStatusItems++;
                        if (mTest && hFile) itemsWithFile++;
                        if (mTest && sMatch && hFile && !string.IsNullOrWhiteSpace(lookupTik)) readyToImport++;
                    }
                    _logger.LogInformation(
                        "TASKDOC TEST SUMMARY | TestTikNumber={TestTikNumber} | MatchedItems={Matched} | SuccessStatusItems={SuccessStatus} | ItemsWithFile={WithFile} | ReadyToImport={Ready}",
                        ts.TestTikNumber, matchedItems, successStatusItems, itemsWithFile, readyToImport);
                    if (matchedItems == 0)
                        _logger.LogWarning("TASKDOC TEST NO MATCH | TestTikNumber={TestTikNumber}", ts.TestTikNumber);
                }

                foreach (var taskItem in taskItems)
                {
                    if (ct.IsCancellationRequested) break;

                    if (ts.IsTestMode &&
                        !string.Equals(taskItem.TikNumber?.Trim(), ts.TestTikNumber!.Trim(), StringComparison.OrdinalIgnoreCase))
                        continue;

                    tasksProcessed++;
                    try
                    {
                        await ProcessTaskItemAsync(taskItem, ct);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "TASKDOC unhandled error | ItemId={ItemId}", taskItem.ItemId);
                    }

                    if (_settings.ItemProcessingDelayMs > 0)
                        await Task.Delay(_settings.ItemProcessingDelayMs, ct);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "TASKDOC fetch failed | BoardId={BoardId}", ts.BoardId);
                SendAlert("Failed to fetch Tasks board", null, ex);
            }

            sourceSw.Stop();
            var imported = _totalSucceeded - prevSucceeded;
            var skipped = _totalSkipped - prevSkipped;
            var failed = _totalFailed - prevFailed;
            _logger.LogInformation(
                "TASKDOC SUMMARY | Fetched={Fetched} | Processed={Processed} | Imported={Imported} | Skipped={Skipped} | Failed={Failed}",
                tasksFetched, tasksProcessed, imported, skipped, failed);
            _logger.LogInformation(
                "DOCINGESTION SOURCE COMPLETE | Source=Tasks, BoardId={BoardId}, Elapsed={ElapsedMs}ms",
                ts.BoardId, sourceSw.ElapsedMilliseconds);
        }

        private static readonly HashSet<string> WordExtensions = new(StringComparer.OrdinalIgnoreCase) { ".docx", ".doc" };

        private static bool IsWordAsset(string? assetName)
        {
            if (string.IsNullOrWhiteSpace(assetName)) return false;
            var ext = Path.GetExtension(assetName);
            return WordExtensions.Contains(ext);
        }

        private async Task ProcessTaskItemAsync(DocumentIngestionMondayService.TaskItem item, CancellationToken ct)
        {
            var ts = _settings.TasksSource!;
            var fileColumnId = ts.FileColumnId;
            var statusMatch = string.Equals(item.StatusLabel?.Trim(), ts.SuccessStatusLabel, StringComparison.Ordinal);
            var lookupTik = item.TikNumber?.Trim() ?? "";
            var statusText = item.StatusLabel ?? "<null>";

            if (!statusMatch)
            {
                _logger.LogDebug(
                    "TASKDOC BLOCKED | Reason=StatusMismatch | ItemId={ItemId} | LookupTikNumber={LookupTikNumber} | StatusText={StatusText}",
                    item.ItemId, string.IsNullOrEmpty(lookupTik) ? "<empty>" : lookupTik, statusText);
                return;
            }

            var wordAssets = item.FileAssets.Where(a => IsWordAsset(a.Name)).ToList();
            var hasWordFile = wordAssets.Count > 0;
            var nonWordCount = item.FileAssets.Count - wordAssets.Count;

            if (nonWordCount > 0)
            {
                _logger.LogDebug(
                    "TASKDOC FILE SOURCE | Column={Column} | ItemId={ItemId} | TotalAssets={Total} | WordAssets={Word} | SkippedNonWord={NonWord}",
                    fileColumnId, item.ItemId, item.FileAssets.Count, wordAssets.Count, nonWordCount);
            }

            if (!hasWordFile)
            {
                var statusChangedAt = item.StatusChangedAtUtc ?? item.ItemUpdatedAtUtc;
                var timedOut = statusChangedAt.HasValue && statusChangedAt.Value < DateTime.UtcNow.AddHours(-ts.FileWaitTimeoutHours);

                if (item.FileAssets.Count > 0)
                {
                    _logger.LogWarning(
                        "TASKDOC ERROR | Reason=NoWordFile | ItemId={ItemId} | LookupTikNumber={LookupTikNumber} | StatusText={StatusText} | TotalAssets={Total} | AssetNames={Names}",
                        item.ItemId, string.IsNullOrEmpty(lookupTik) ? "<empty>" : lookupTik, statusText,
                        item.FileAssets.Count, string.Join(", ", item.FileAssets.Select(a => a.Name)));
                    _totalFailed++;
                }
                else
                {
                    _logger.LogDebug(
                        "TASKDOC BLOCKED | Reason=MissingWordFile | ItemId={ItemId} | LookupTikNumber={LookupTikNumber} | StatusText={StatusText}",
                        item.ItemId, string.IsNullOrEmpty(lookupTik) ? "<empty>" : lookupTik, statusText);
                    if (timedOut)
                    {
                        await MarkTaskTimeoutNoFileAsync(item.ItemId, fileColumnId, ct);
                        _totalFailed++;
                    }
                }
                return;
            }

            if (string.IsNullOrWhiteSpace(item.TikNumber))
            {
                _logger.LogDebug(
                    "TASKDOC BLOCKED | Reason=LookupTikNumberMissing | ItemId={ItemId} | LookupTikNumber={LookupTikNumber} | StatusText={StatusText}",
                    item.ItemId, "<empty>", statusText);
                await MarkTaskMissingTikFailureAsync(item, fileColumnId, ct);
                _totalFailed++;
                return;
            }

            int? tikCounter;
            try
            {
                tikCounter = await _documentWriter.ResolveTikCounterAsync(item.TikNumber!, ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "TASKDOC resolve failed | ItemId={ItemId}, TikNumber={TikNumber}", item.ItemId, item.TikNumber);
                SendAlert($"Tasks: TikCounter resolution failed for {item.TikNumber}", null, ex);
                _totalFailed++;
                return;
            }

            if (!tikCounter.HasValue)
            {
                _logger.LogDebug(
                    "TASKDOC BLOCKED | Reason=NoTikCounter | ItemId={ItemId} | LookupTikNumber={LookupTikNumber} | StatusText={StatusText}",
                    item.ItemId, item.TikNumber, statusText);
                await MarkTaskMissingTikFailureAsync(item, fileColumnId, ct);
                _totalFailed++;
                return;
            }

            // ── Resolve DocumentType from TikVisualID prefix (business client number) ──
            var clientNumber = DocumentTypeMap.ParseClientNumber(item.TikNumber, '/');
            string documentType;
            string derivationPath;

            if (clientNumber.HasValue)
            {
                var (resolved, path) = DocumentTypeMap.ResolveDocumentType(clientNumber.Value);
                derivationPath = path;
                documentType = resolved ?? DocumentTypeMap.Resolve(fileColumnId);
            }
            else
            {
                documentType = DocumentTypeMap.Resolve(fileColumnId);
                derivationPath = "LegacyFallback";
            }

            if (derivationPath == "Unresolved")
            {
                _logger.LogWarning(
                    "TASKDOC DOCTYPE | Path={Path} | TikCounter={TikCounter} | TikVisualID={TikVisualID} | ClientNumber={ClientNumber} | DocumentType={DocumentType}",
                    derivationPath, tikCounter.Value, item.TikNumber, clientNumber?.ToString() ?? "<null>", documentType);
            }
            else if (derivationPath == "LegacyFallback")
            {
                _logger.LogWarning(
                    "TASKDOC DOCTYPE | Path={Path} | TikCounter={TikCounter} | TikVisualID={TikVisualID} | ClientNumber={ClientNumber} | DocumentType={DocumentType}",
                    derivationPath, tikCounter.Value, item.TikNumber, clientNumber?.ToString() ?? "<null>", documentType);
            }
            else
            {
                _logger.LogInformation(
                    "TASKDOC DOCTYPE | Path={Path} | TikCounter={TikCounter} | TikVisualID={TikVisualID} | ClientNumber={ClientNumber} | DocumentType={DocumentType}",
                    derivationPath, tikCounter.Value, item.TikNumber, clientNumber, documentType);
            }

            var assetRef = wordAssets[0];
            var assetExt = Path.GetExtension(assetRef.Name)?.TrimStart('.').ToLowerInvariant() ?? "docx";
            _logger.LogDebug(
                "TASKDOC IMPORT START | ItemId={ItemId} | TikNumber={TikNumber} | TikCounter={TikCounter} | AssetId={AssetId} | ColumnId={ColumnId} | Extension={Ext} | AssetName={AssetName}",
                item.ItemId, item.TikNumber, tikCounter.Value, assetRef.AssetId, fileColumnId, assetExt, assetRef.Name);

            await ProcessSingleAssetAsync(item.ItemId, 0, fileColumnId, assetRef, item.TikNumber!, tikCounter.Value, ct, documentType);
        }

        private async Task MarkTaskTimeoutNoFileAsync(long itemId, string columnId, CancellationToken ct)
        {
            const string sentinelAssetId = "TIMEOUT";
            var existing = await _integrationDb.MondayDocumentImports
                .FirstOrDefaultAsync(r =>
                    r.MondayQuestionnaireItemId == itemId &&
                    r.ColumnId == columnId &&
                    r.AssetId == sentinelAssetId, ct);

            if (existing != null)
            {
                existing.Status = DocumentImportStatus.Failed;
                existing.ErrorMessage = $"Timeout: {_settings.TasksSource!.FileWaitTimeoutHours}h passed since status ready, no file";
                existing.UpdatedAtUtc = DateTime.UtcNow;
            }
            else
            {
                _integrationDb.MondayDocumentImports.Add(new MondayDocumentImport
                {
                    CreatedAtUtc = DateTime.UtcNow,
                    UpdatedAtUtc = DateTime.UtcNow,
                    MondayQuestionnaireItemId = itemId,
                    LinkedCaseItemId = null,
                    ColumnId = columnId,
                    AssetId = sentinelAssetId,
                    TikVisualID = null,
                    OriginalFileName = "",
                    Status = DocumentImportStatus.Failed,
                    ErrorMessage = $"Timeout: {_settings.TasksSource!.FileWaitTimeoutHours}h passed since status ready, no file"
                });
            }
            await _integrationDb.SaveChangesAsync(ct);
        }

        private async Task MarkTaskMissingTikFailureAsync(
            DocumentIngestionMondayService.TaskItem item,
            string columnId,
            CancellationToken ct)
        {
            var assetRef = item.FileAssets.Count > 0 ? item.FileAssets[0] : null;
            var assetIdStr = assetRef?.AssetId.ToString() ?? "NO_TIK";
            var existing = await _integrationDb.MondayDocumentImports
                .FirstOrDefaultAsync(r =>
                    r.MondayQuestionnaireItemId == item.ItemId &&
                    r.ColumnId == columnId &&
                    r.AssetId == assetIdStr, ct);

            if (existing != null)
            {
                if (existing.Status == DocumentImportStatus.Success) return;
                existing.Status = DocumentImportStatus.Failed;
                existing.ErrorMessage = "TikNumber missing or empty in lookup column";
                existing.UpdatedAtUtc = DateTime.UtcNow;
            }
            else
            {
                _integrationDb.MondayDocumentImports.Add(new MondayDocumentImport
                {
                    CreatedAtUtc = DateTime.UtcNow,
                    UpdatedAtUtc = DateTime.UtcNow,
                    MondayQuestionnaireItemId = item.ItemId,
                    LinkedCaseItemId = null,
                    ColumnId = columnId,
                    AssetId = assetIdStr,
                    TikVisualID = item.TikNumber,
                    OriginalFileName = assetRef?.Name ?? "",
                    Status = DocumentImportStatus.Failed,
                    ErrorMessage = "TikNumber missing or empty in lookup column"
                });
            }
            await _integrationDb.SaveChangesAsync(ct);
        }

        private async Task ProcessQuestionnaireItemAsync(
            DocumentIngestionMondayService.QuestionnaireItem item, CancellationToken ct, string? runId = null)
        {
            var hasFiles = item.FileColumns.Count > 0;
            var accidentStoryEnabled = _settings.IsAccidentStoryEnabled;

            if (!hasFiles && !accidentStoryEnabled)
            {
                _logger.LogDebug("DOCINGESTION item {ItemId} has no file columns and accident story disabled, skipping", item.ItemId);
                return;
            }

            if (item.LinkedCaseItemIds.Count == 0)
            {
                _logger.LogWarning("DOCINGESTION item {ItemId} ('{Name}') has no linked case item, skipping",
                    item.ItemId, item.Name);
                _totalSkipped += item.FileColumns.Values.Sum(f => f.Count);
                return;
            }

            var linkedCaseItemId = item.LinkedCaseItemIds[0];

            string? tikVisualID;
            try
            {
                tikVisualID = await _mondayService.GetItemColumnTextAsync(
                    linkedCaseItemId, _settings.LinkedCaseTikColumnId, ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "DOCINGESTION failed to get TikVisualID from linked case item {LinkedItemId} for questionnaire item {ItemId}",
                    linkedCaseItemId, item.ItemId);
                _totalSkipped += item.FileColumns.Values.Sum(f => f.Count);
                return;
            }

            if (string.IsNullOrWhiteSpace(tikVisualID))
            {
                _logger.LogWarning(
                    "DOCINGESTION linked case item {LinkedItemId} has empty TikVisualID (column {ColId}), skipping item {ItemId}",
                    linkedCaseItemId, _settings.LinkedCaseTikColumnId, item.ItemId);
                _totalSkipped += item.FileColumns.Values.Sum(f => f.Count);
                return;
            }

            int? tikCounter;
            try
            {
                tikCounter = await _documentWriter.ResolveTikCounterAsync(tikVisualID, ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "DOCINGESTION failed to resolve TikCounter for TikVisualID={TikVisualID}, item {ItemId}",
                    tikVisualID, item.ItemId);
                SendAlert($"TikCounter resolution failed for {tikVisualID}", null, ex);
                _totalSkipped += item.FileColumns.Values.Sum(f => f.Count);
                return;
            }

            if (!tikCounter.HasValue)
            {
                _logger.LogWarning(
                    "DOCINGESTION no TikCounter found for TikVisualID={TikVisualID}, linked item {LinkedItemId}, questionnaire item {ItemId}",
                    tikVisualID, linkedCaseItemId, item.ItemId);

                foreach (var (colId, assets) in item.FileColumns)
                    foreach (var asset in assets)
                        await MarkMissingTikFailure(item.ItemId, linkedCaseItemId, colId, asset, tikVisualID);

                _totalFailed += item.FileColumns.Values.Sum(f => f.Count);
                return;
            }

            _logger.LogDebug(
                "DOCINGESTION resolved TikVisualID={TikVisualID} → TikCounter={TikCounter} for item {ItemId}",
                tikVisualID, tikCounter.Value, item.ItemId);

            if (hasFiles)
            {
                foreach (var (columnId, assets) in item.FileColumns)
                {
                    foreach (var asset in assets)
                    {
                        if (ct.IsCancellationRequested) return;
                        await ProcessSingleAssetAsync(
                            item.ItemId, linkedCaseItemId, columnId, asset,
                            tikVisualID, tikCounter.Value, ct);
                    }
                }
            }

            if (accidentStoryEnabled)
            {
                await ProcessAccidentStoryAsync(
                    item.ItemId, linkedCaseItemId, tikVisualID, tikCounter.Value, ct, runId);
            }
        }

        private async Task ProcessSingleAssetAsync(
            long questionnaireItemId, long linkedCaseItemId, string columnId,
            DocumentIngestionMondayService.FileAssetRef assetRef,
            string tikVisualID, int tikCounter, CancellationToken ct,
            string? documentType = null)
        {
            _totalProcessed++;
            var assetIdStr = assetRef.AssetId.ToString();
            var assetSw = Stopwatch.StartNew();

            if (string.IsNullOrWhiteSpace(tikVisualID) || tikCounter <= 0)
            {
                _logger.LogWarning("DOC SKIP PREINSERT | Reason=MissingTikNumber | ItemId={ItemId}, AssetId={AssetId}, TikVisualID={TikVisualID}, TikCounter={TikCounter}",
                    questionnaireItemId, assetIdStr, tikVisualID ?? "<null>", tikCounter);
                _totalSkipped++;
                return;
            }

            if (linkedCaseItemId == 0 && string.IsNullOrWhiteSpace(columnId))
            {
                _logger.LogWarning("DOC SKIP PREINSERT | Reason=MissingCaseMapping | ItemId={ItemId}, AssetId={AssetId}, LinkedCaseItemId=0, ColumnId={ColumnId}",
                    questionnaireItemId, assetIdStr, columnId ?? "<null>");
                _totalSkipped++;
                return;
            }

            var record = await GetOrCreateTrackingRecordAsync(
                questionnaireItemId, columnId, assetIdStr,
                tikVisualID, tikCounter, linkedCaseItemId, assetRef.Name);

            if (record.Status == DocumentImportStatus.Success)
            {
                _duplicateSkipCount++;
                _totalSkipped++;
                if (linkedCaseItemId == 0)
                    _logger.LogDebug(
                        "TASKDOC IMPORT SKIPPED | Reason=Duplicate | ItemId={ItemId}, AssetId={AssetId}, TikNumber={TikNumber}, TikCounter={TikCounter}",
                        questionnaireItemId, assetIdStr, tikVisualID, tikCounter);
                return;
            }

            var maxAttempts = _settings.MaxRetryCount;
            if (record.Status == DocumentImportStatus.Failed && record.RetryCount >= maxAttempts)
            {
                _logger.LogWarning(
                    "DOCINGESTION EXCEEDED MAX RETRIES | AssetId={AssetId}, ItemId={ItemId}, TikCounter={TikCounter}, TikVisualID={TikVisualID}, LastError={LastError}, LastStatus={LastStatus}",
                    assetIdStr, questionnaireItemId, tikCounter, tikVisualID, record.ErrorMessage ?? "unknown", record.Status);
                _totalSkipped++;
                return;
            }

            record.LastAttemptAtUtc = DateTime.UtcNow;
            if (record.Status == DocumentImportStatus.Failed)
                record.RetryCount++;

            var attempt = record.RetryCount + 1;
            var attemptLabel = $"Attempt {attempt}/{maxAttempts}";

            _logger.LogDebug(
                "DOCINGESTION START | TikVisualID={TikVisualID}, TikCounter={TikCounter}, ItemId={ItemId}, ColId={ColId}, AssetId={AssetId}, OriginalFileNameLog={OriginalFileNameLog}, {Attempt}",
                tikVisualID, tikCounter, questionnaireItemId, columnId, assetIdStr, SafeFileNameForLog(assetRef.Name), attemptLabel);

            var currentStage = "DOWNLOAD";
            try
            {
                // Step 1: Download (skip if already downloaded and file exists)
                if (record.Status < DocumentImportStatus.Downloaded ||
                    string.IsNullOrEmpty(record.InboxFilePath) ||
                    !File.Exists(record.InboxFilePath))
                {
                    currentStage = "DOWNLOAD";
                    await DownloadAssetToInboxAsync(record, assetRef.AssetId, tikVisualID, tikCounter, ct, attempt, maxAttempts);
                }
                else
                {
                    _logger.LogDebug("DOCINGESTION asset {AssetId} already in inbox at {Path}, skipping download",
                        assetIdStr, record.InboxFilePath);
                }

                // Step 2: Create Odcanit document row (skip if already created)
                if (record.Status < DocumentImportStatus.SpCreated ||
                    !record.OdcanitDocCounter.HasValue ||
                    string.IsNullOrEmpty(record.OdcanitDestPath))
                {
                    // Validation guard before writing to Odcanit: file size > 0; for PDF also size > 1024 and %PDF magic
                    if (!string.IsNullOrEmpty(record.InboxFilePath) && File.Exists(record.InboxFilePath))
                    {
                        var ext = Path.GetExtension(record.InboxFilePath)?.TrimStart('.').ToLowerInvariant() ?? "";
                        if (record.FileSizeBytes <= 0)
                        {
                            record.RetryCount = maxAttempts;
                            record.Status = DocumentImportStatus.Failed;
                            record.ErrorMessage = "EmptyFile; file size is 0";
                            record.UpdatedAtUtc = DateTime.UtcNow;
                            await SaveRecordAsync(record);
                            _totalSkipped++;
                                _logger.LogWarning(
                                "DOCINGESTION SKIP validation failed | AssetId={AssetId}, ItemId={ItemId}, TikCounter={TikCounter}, Reason=EmptyFile",
                                assetIdStr, questionnaireItemId, tikCounter);
                            if (linkedCaseItemId == 0)
                                _logger.LogWarning("TASKDOC IMPORT FAILED | ItemId={ItemId}, TikNumber={TikNumber}, TikCounter={TikCounter}, AssetId={AssetId}, ColumnId={ColumnId}, Reason=EmptyFile",
                                    questionnaireItemId, tikVisualID, tikCounter, assetIdStr, columnId);
                            return;
                        }
                        if (string.Equals(ext, "pdf", StringComparison.OrdinalIgnoreCase))
                        {
                            if (record.FileSizeBytes <= 1024 || !VerifyPdfMagicBytes(record.InboxFilePath))
                            {
                                record.RetryCount = maxAttempts;
                                record.Status = DocumentImportStatus.Failed;
                                record.ErrorMessage = "PdfValidationFailed; size<=1024 or file does not start with %PDF";
                                record.UpdatedAtUtc = DateTime.UtcNow;
                                await SaveRecordAsync(record);
                                _totalSkipped++;
                                _logger.LogWarning(
                                    "DOCINGESTION SKIP validation failed | AssetId={AssetId}, ItemId={ItemId}, TikCounter={TikCounter}, Reason=WriteFailure (PDF guard: size>1024 and %PDF magic required)",
                                    assetIdStr, questionnaireItemId, tikCounter);
                                if (linkedCaseItemId == 0)
                                    _logger.LogWarning("TASKDOC IMPORT FAILED | ItemId={ItemId}, TikNumber={TikNumber}, TikCounter={TikCounter}, AssetId={AssetId}, ColumnId={ColumnId}, Reason=PdfValidationFailed",
                                        questionnaireItemId, tikVisualID, tikCounter, assetIdStr, columnId);
                                return;
                            }
                        }
                    }

                    currentStage = "UPLOAD";
                    _logger.LogDebug(
                        "DOCINGESTION STAGE=UPLOAD | AssetId={AssetId}, ItemId={ItemId}, TikCounter={TikCounter}, TikVisualID={TikVisualID}, {Attempt}",
                        assetIdStr, questionnaireItemId, tikCounter, tikVisualID, attemptLabel);
                    await CreateDocumentRowAsync(record, tikCounter, tikVisualID, columnId, ct, documentType);
                    await SaveRecordAsync(record);
                    await CopyToDestPathAsync(record, ct);
                    _logger.LogDebug(
                        "DOCINGESTION STAGE=UPLOAD SUCCESS | AssetId={AssetId}, ItemId={ItemId}, TikCounter={TikCounter}, DocCounter={DocCounter}, DestPath={DestPath}",
                        assetIdStr, questionnaireItemId, tikCounter, record.OdcanitDocCounter, record.OdcanitDestPath);
                }
                else
                {
                    _logger.LogDebug("DOCINGESTION asset {AssetId} SP already called, DocCounter={DocCounter}",
                        assetIdStr, record.OdcanitDocCounter);
                }

                // Step 3: Copy to DestPath (if not done above)
                if (record.Status < DocumentImportStatus.Copied)
                {
                    currentStage = "UPLOAD";
                    await CopyToDestPathAsync(record, ct);
                }

                // Step 4: Verify
                if (record.Status < DocumentImportStatus.Verified)
                {
                    VerifyCopy(record);
                }

                // Success persisted before inbox delete
                record.Status = DocumentImportStatus.Success;
                record.ErrorMessage = null;
                record.UpdatedAtUtc = DateTime.UtcNow;
                await SaveRecordAsync(record);

                CleanupInboxFile(record);

                assetSw.Stop();
                _totalSucceeded++;
                _logger.LogDebug(
                    "DOCINGESTION SUCCESS | TikVisualID={TikVisualID}, TikCounter={TikCounter}, ItemId={ItemId}, ColId={ColId}, AssetId={AssetId}, DocCounter={DocCounter}, DestPath={DestPath}, Elapsed={ElapsedMs}ms",
                    tikVisualID, tikCounter, questionnaireItemId, columnId, assetIdStr,
                    record.OdcanitDocCounter, record.OdcanitDestPath, assetSw.ElapsedMilliseconds);
                if (linkedCaseItemId == 0)
                    _logger.LogInformation(
                        "TASKDOC IMPORT SUCCESS | ItemId={ItemId}, TikNumber={TikNumber}, TikCounter={TikCounter}, AssetId={AssetId}, ColumnId={ColumnId}, DocCounter={DocCounter}, DestPath={DestPath}",
                        questionnaireItemId, tikVisualID, tikCounter, assetIdStr, columnId, record.OdcanitDocCounter, record.OdcanitDestPath);
            }
            catch (OversizedFileException ofx)
            {
                assetSw.Stop();
                record.RetryCount = maxAttempts;
                record.Status = DocumentImportStatus.Failed;
                record.ErrorMessage = ofx.Message.Length > 2000 ? ofx.Message[..2000] : ofx.Message;
                record.UpdatedAtUtc = DateTime.UtcNow;
                await SaveRecordAsync(record);
                _totalSkipped++;
                _logger.LogWarning(
                    "DOCINGESTION SKIP file too large (no retries) | AssetId={AssetId}, ItemId={ItemId}, TikCounter={TikCounter}, TikVisualID={TikVisualID}, ColumnId={ColumnId}, FileName={FileName}, FileSize={FileSize}, MaxSize={MaxSize}, Reason=FILE_TOO_LARGE",
                    assetIdStr, questionnaireItemId, tikCounter, tikVisualID, columnId, record.OriginalFileName, ofx.FileSize, ofx.MaxSize);
                if (linkedCaseItemId == 0)
                    _logger.LogWarning("TASKDOC IMPORT FAILED | ItemId={ItemId}, TikNumber={TikNumber}, TikCounter={TikCounter}, AssetId={AssetId}, ColumnId={ColumnId}, Reason=FILE_TOO_LARGE",
                        questionnaireItemId, tikVisualID, tikCounter, assetIdStr, columnId);
                return;
            }
            catch (InvalidExtensionException iex)
            {
                assetSw.Stop();
                record.RetryCount = maxAttempts;
                record.Status = DocumentImportStatus.Failed;
                record.ErrorMessage = iex.Message.Length > 2000 ? iex.Message[..2000] : iex.Message;
                record.UpdatedAtUtc = DateTime.UtcNow;
                await SaveRecordAsync(record);
                _totalSkipped++;
                _logger.LogWarning(
                    "DOCINGESTION SKIP extension rejected (no retries) | AssetId={AssetId}, ItemId={ItemId}, TikCounter={TikCounter}, TikVisualID={TikVisualID}, ColumnId={ColumnId}, FileName={FileName}, Extension={Extension}, Reason={Reason}",
                    assetIdStr, questionnaireItemId, tikCounter, tikVisualID, columnId, record.OriginalFileName, iex.DetectedExtension ?? "", iex.Reason);
                if (linkedCaseItemId == 0)
                    _logger.LogWarning("TASKDOC IMPORT FAILED | ItemId={ItemId}, TikNumber={TikNumber}, TikCounter={TikCounter}, AssetId={AssetId}, ColumnId={ColumnId}, Reason={Reason}",
                        questionnaireItemId, tikVisualID, tikCounter, assetIdStr, columnId, iex.Reason);
                return;
            }
            catch (Exception ex)
            {
                assetSw.Stop();
                _totalFailed++;
                record.Status = DocumentImportStatus.Failed;
                record.ErrorMessage = ex.Message.Length > 2000 ? ex.Message[..2000] : ex.Message;
                record.UpdatedAtUtc = DateTime.UtcNow;

                _logger.LogError(ex,
                    "DOCINGESTION FAILED | STAGE={Stage}, AssetId={AssetId}, ItemId={ItemId}, TikCounter={TikCounter}, TikVisualID={TikVisualID}, {Attempt}, Error={Error}, Elapsed={ElapsedMs}ms",
                    currentStage, assetIdStr, questionnaireItemId, tikCounter, tikVisualID, attemptLabel, ex.Message, assetSw.ElapsedMilliseconds);
                if (linkedCaseItemId == 0)
                    _logger.LogError(
                        "TASKDOC IMPORT FAILED | ItemId={ItemId}, TikNumber={TikNumber}, TikCounter={TikCounter}, AssetId={AssetId}, ColumnId={ColumnId}, Stage={Stage}, Error={Error}",
                        questionnaireItemId, tikVisualID, tikCounter, assetIdStr, columnId, currentStage, ex.Message);

                if (!record.AlertSent)
                {
                    SendAlert(
                        $"Document ingestion failed: AssetId={assetIdStr}, TikCounter={tikCounter}, Stage={currentStage}",
                        record, ex);
                    record.AlertSent = true;
                }

                await SaveRecordAsync(record);
            }
        }

        // ───────── Step 1: Download ─────────
        // Pre-signed URLs (e.g. S3 with X-Amz-Signature) are invalidated by any change. Use the exact URL
        // from Monday; do not add query params (e.g. response-content-disposition) or parse/rebuild the URL.
        // Manual test: Re-run worker for AssetId=198847023, ItemId=2728213714; expect HTTP 200 and file saved.
        // If 403: check UrlWasModified=false and UrlHashPrefix change between attempts (fresh URL per retry).

        private async Task DownloadAssetToInboxAsync(
            MondayDocumentImport record, long assetId, string tikVisualID, int tikCounter, CancellationToken ct,
            int attempt, int maxAttempts)
        {
            var dlSw = Stopwatch.StartNew();
            var attemptLabel = $"Attempt {attempt}/{maxAttempts}";

            _logger.LogDebug(
                "DOCINGESTION STAGE=DOWNLOAD | AssetId={AssetId}, ItemId={ItemId}, TikCounter={TikCounter}, TikVisualID={TikVisualID}, {Attempt}",
                assetId, record.MondayQuestionnaireItemId, tikCounter, tikVisualID, attemptLabel);

            var assetInfo = await _mondayService.GetAssetDownloadInfoAsync(assetId, ct);
            if (assetInfo == null || string.IsNullOrWhiteSpace(assetInfo.PublicUrl))
                throw new InvalidOperationException($"Cannot obtain download URL for asset {assetId}");

            record.OriginalFileName = assetInfo.Name ?? string.Empty;

            var rawExt = Path.GetExtension(assetInfo.Name ?? "")?.TrimStart('.').ToLowerInvariant();
            if (_settings.IsDeniedExtension(rawExt) || _settings.IsDeniedExtension(assetInfo.FileExtension))
            {
                var deniedExt = _settings.IsDeniedExtension(rawExt) ? rawExt : assetInfo.FileExtension;
                throw new InvalidExtensionException(
                    $"DENYLIST_EXTENSION; asset {assetId} has denied extension '{deniedExt}' (file: {assetInfo.Name}, column: {record.ColumnId})",
                    reason: "DENYLIST_EXTENSION",
                    detectedExtension: deniedExt);
            }

            var maxSizeForColumn = _settings.GetMaxFileSizeForColumn(record.ColumnId);
            if (assetInfo.FileSize > maxSizeForColumn)
                throw new OversizedFileException(
                    $"FILE_TOO_LARGE; asset {assetId} size {assetInfo.FileSize} exceeds max {maxSizeForColumn} bytes (file: {assetInfo.Name}, column: {record.ColumnId})",
                    assetInfo.FileSize, maxSizeForColumn);

            // Use the exact URL from Monday as an opaque string; never add/remove/normalize query params.
            var downloadUrl = assetInfo.PublicUrl;
            var (urlHashPrefix, urlLength, hasAmzSignature) = GetDownloadUrlDiagnostics(downloadUrl);
            const bool urlWasModified = false;
            _logger.LogDebug(
                "DOCINGESTION DOWNLOAD URL (opaque) | AssetId={AssetId}, UrlHashPrefix={UrlHashPrefix}, UrlLength={UrlLength}, HasAmzSignature={HasAmzSignature}, UrlWasModified={UrlWasModified}",
                assetId, urlHashPrefix, urlLength, hasAmzSignature, urlWasModified);

            var allowlist = _settings.AllowedExtensions;
            ExtensionDetectionResult? detection = TryDetectExtensionFromMetadata(assetInfo.Name, assetInfo.FileExtension, allowlist);

            using var downloadClient = _httpClientFactory.CreateClient("MondayFileDownload");
            HttpResponseMessage? response = null;
            try
            {
                response = await downloadClient.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead, ct);
                response.EnsureSuccessStatusCode();
            }
            catch (HttpRequestException ex)
            {
                var statusCode = response?.StatusCode;
                var statusCodeInt = statusCode.HasValue ? (int)statusCode.Value : 0;
                _logger.LogWarning(ex,
                    "DOCINGESTION STAGE=DOWNLOAD FAILED | AssetId={AssetId}, ItemId={ItemId}, TikCounter={TikCounter}, {Attempt}, StatusCode={StatusCode}, UrlHashPrefix={UrlHashPrefix}, UrlLength={UrlLength}, HasAmzSignature={HasAmzSignature}, UrlWasModified={UrlWasModified}, Error={Error}",
                    assetId, record.MondayQuestionnaireItemId, tikCounter, attemptLabel, statusCodeInt, urlHashPrefix, urlLength, hasAmzSignature, urlWasModified, ex.Message);
                if (statusCodeInt == 403)
                    _logger.LogWarning(
                        "DOCINGESTION 403: If UrlWasModified=false and URL is fresh per attempt, likely cause is permission/scope or expired pre-signed URL; otherwise check for URL tampering.");
                throw;
            }

            if (detection == null)
            {
                var contentDisposition = response!.Content.Headers.ContentDisposition?.ToString()
                    ?? (response.Headers.TryGetValues("Content-Disposition", out var cdVals) ? cdVals.FirstOrDefault() : null);
                detection = TryDetectExtensionFromContentDisposition(contentDisposition, allowlist);
            }
            if (detection == null)
                detection = TryDetectExtensionFromContentType(response!.Content.Headers.ContentType?.MediaType, allowlist);

            byte[]? magicBuffer = null;
            Stream? contentStreamForMagic = null;
            if (detection == null)
            {
                contentStreamForMagic = await response.Content.ReadAsStreamAsync(ct);
                magicBuffer = new byte[12];
                var read = await contentStreamForMagic.ReadAsync(magicBuffer.AsMemory(0, 12), ct);
                detection = TryDetectExtensionFromMagicBytes(magicBuffer.AsSpan(0, read).ToArray(), allowlist);
                if (detection == null)
                {
                    var rawNameExt = Path.GetExtension(assetInfo.Name ?? "")?.TrimStart('.').ToLowerInvariant();
                    var hasRecognizableExt = !string.IsNullOrEmpty(rawNameExt) && rawNameExt.Length <= MaxExtensionLength;
                    var reason = hasRecognizableExt ? "UNSUPPORTED_EXTENSION" : "UNKNOWN_MEDIA_SIGNATURE";
                    _logger.LogWarning(
                        "DOCINGESTION SKIP no valid extension | AssetId={AssetId}, ItemId={ItemId}, assetName={AssetName}, assetFileExtension={AssetFileExtension}, detectedExtension=, detectionSource=, Allowlist=[{Allowlist}], Reason={Reason}",
                        assetId, record.MondayQuestionnaireItemId, SafeFileNameForLog(assetInfo.Name), assetInfo.FileExtension ?? "", string.Join(",", allowlist), reason);
                    await contentStreamForMagic.DisposeAsync();
                    throw new InvalidExtensionException(
                        $"{reason}; no allowed extension for asset {assetId}. Name/file_extension not in allowlist and content-type/magic did not resolve. (rawExt={rawNameExt})",
                        reason: reason,
                        detectedExtension: hasRecognizableExt ? rawNameExt : null);
                }
                if (read < 12)
                    magicBuffer = null;
            }

            var ext = detection.Extension;
            record.LastDetectionSource = detection.DetectionSource;
            record.LastDetectedMimeType = detection.MimeType;
            var detectedMimeForLog = detection.MimeType ?? "";
            _logger.LogDebug(
                "DOCINGESTION extension detection | AssetId={AssetId}, ColumnId={ColumnId}, TikCounter={TikCounter}, DetectedExtension={DetectedExtension}, DetectionMethod={DetectionMethod}",
                assetId, record.ColumnId, tikCounter, ext, detection.DetectionSource);

            var safeFileName = AssetSafeFileName(assetId, ext);

            var safeTikDir = SanitizeTikVisualID(tikVisualID);
            var caseFolderPath = Path.Combine(_settings.InboxPath, "Cases", safeTikDir);
            Directory.CreateDirectory(caseFolderPath);
            var targetPath = Path.Combine(caseFolderPath, safeFileName);
            var tempPath = targetPath + ".tmp";

            if (magicBuffer != null && contentStreamForMagic != null)
            {
                await using (contentStreamForMagic)
                await using (var fileStream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920))
                {
                    await fileStream.WriteAsync(magicBuffer, ct);
                    await contentStreamForMagic.CopyToAsync(fileStream, ct);
                }
            }
            else
            {
                await using (var contentStream = await response.Content.ReadAsStreamAsync(ct))
                await using (var fileStream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920))
                {
                    await contentStream.CopyToAsync(fileStream, ct);
                }
            }

            if (File.Exists(targetPath))
                File.Delete(targetPath);
            File.Move(tempPath, targetPath);

            if (string.Equals(ext, "pdf", StringComparison.OrdinalIgnoreCase) && !VerifyPdfMagicBytes(targetPath))
                throw new InvalidOperationException($"Asset {assetId}: file does not have PDF magic bytes (%PDF) at path {targetPath}");

            var fileInfo = new FileInfo(targetPath);
            record.InboxFilePath = targetPath;
            record.FileSizeBytes = fileInfo.Length;
            record.Status = DocumentImportStatus.Downloaded;
            record.UpdatedAtUtc = DateTime.UtcNow;

            dlSw.Stop();
            _logger.LogDebug(
                "DOCINGESTION STAGE=DOWNLOAD SUCCESS | AssetId={AssetId}, ItemId={ItemId}, TikCounter={TikCounter}, Size={Size}, Elapsed={ElapsedMs}ms",
                assetId, record.MondayQuestionnaireItemId, tikCounter, fileInfo.Length, dlSw.ElapsedMilliseconds);
        }

        private static bool VerifyPdfMagicBytes(string filePath)
        {
            try
            {
                using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
                if (fs.Length < 5) return false;
                var buf = new byte[5];
                var read = fs.Read(buf, 0, 5);
                if (read < 4) return false;
                return buf[0] == 0x25 && buf[1] == 0x50 && buf[2] == 0x44 && buf[3] == 0x46; // %PDF
            }
            catch
            {
                return false;
            }
        }

        // ───────── Step 2: Create Odcanit document row ─────────

        private async Task CreateDocumentRowAsync(
            MondayDocumentImport record, int tikCounter, string tikVisualID, string columnId, CancellationToken ct,
            string? documentType = null)
        {
            var spSw = Stopwatch.StartNew();

            var resolvedType = documentType ?? DocumentTypeMap.Resolve(columnId);
            var extension = Path.GetExtension(record.InboxFilePath!) ?? ".pdf";
            var businessFileName = DocumentTypeMap.BuildBusinessFileName(tikCounter, tikVisualID, resolvedType, extension);
            _logger.LogInformation(
                "Resolved Odcanit filename | Tik={TikVisualID} | DocumentType={DocumentType} | FinalName={FinalName}",
                tikVisualID, resolvedType, businessFileName);

            // SP constructs DestPath by appending extension from @FilePath — pass name without extension to avoid duplication
            var spFileName = Path.GetFileNameWithoutExtension(businessFileName);
            var result = await _documentWriter.CreateDocumentRowAsync(
                tikCounter, spFileName, record.InboxFilePath!, ct);

            record.OdcanitDocCounter = result.DocCounter;
            record.OdcanitDestPath = result.DestPath;
            record.Status = DocumentImportStatus.SpCreated;
            record.UpdatedAtUtc = DateTime.UtcNow;

            spSw.Stop();
            _logger.LogDebug(
                "DOCINGESTION SP CREATED | AssetId={AssetId}, DocCounter={DocCounter}, DestPath={DestPath}, Elapsed={ElapsedMs}ms",
                record.AssetId, result.DocCounter, result.DestPath, spSw.ElapsedMilliseconds);
        }

        // ───────── Step 3: Copy to DestPath ─────────

        private async Task CopyToDestPathAsync(MondayDocumentImport record, CancellationToken ct)
        {
            var copySw = Stopwatch.StartNew();
            var destPath = record.OdcanitDestPath!;
            var sourcePath = record.InboxFilePath!;

            var destDir = Path.GetDirectoryName(destPath);
            if (!string.IsNullOrEmpty(destDir) && !Directory.Exists(destDir))
                Directory.CreateDirectory(destDir);

            await using (var sourceStream = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920))
            await using (var destStream = new FileStream(destPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920))
            {
                await sourceStream.CopyToAsync(destStream, ct);
            }

            record.Status = DocumentImportStatus.Copied;
            record.UpdatedAtUtc = DateTime.UtcNow;

            copySw.Stop();
            _logger.LogDebug(
                "DOCINGESTION COPIED | AssetId={AssetId}, DestPath={DestPath}, Elapsed={ElapsedMs}ms",
                record.AssetId, destPath, copySw.ElapsedMilliseconds);
        }

        // ───────── Step 4: Verify ─────────

        private void VerifyCopy(MondayDocumentImport record)
        {
            var destPath = record.OdcanitDestPath!;

            if (!File.Exists(destPath))
                throw new IOException($"Verification failed: destination file does not exist at {destPath}");

            var destInfo = new FileInfo(destPath);
            var sourceInfo = new FileInfo(record.InboxFilePath!);

            if (destInfo.Length != sourceInfo.Length)
            {
                File.Delete(destPath);
                throw new IOException(
                    $"Verification failed: size mismatch. Source={sourceInfo.Length}, Dest={destInfo.Length} at {destPath}");
            }

            record.Status = DocumentImportStatus.Verified;
            record.UpdatedAtUtc = DateTime.UtcNow;

            _logger.LogDebug(
                "DOCINGESTION VERIFIED | AssetId={AssetId}, DestPath={DestPath}, Size={Size}",
                record.AssetId, destPath, destInfo.Length);
        }

        // ───────── Step 5: Cleanup ─────────

        private void CleanupInboxFile(MondayDocumentImport record)
        {
            if (string.IsNullOrEmpty(record.InboxFilePath)) return;

            try
            {
                if (File.Exists(record.InboxFilePath))
                {
                    File.Delete(record.InboxFilePath);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "DOCINGESTION failed to delete inbox file {Path} (non-critical)", record.InboxFilePath);
            }
        }

        // ───────── Tracking record management ─────────

        private async Task<MondayDocumentImport> GetOrCreateTrackingRecordAsync(
            long questionnaireItemId, string columnId, string assetId,
            string tikVisualID, int tikCounter, long linkedCaseItemId, string fileName)
        {
            var existing = await _integrationDb.MondayDocumentImports
                .FirstOrDefaultAsync(r =>
                    r.MondayQuestionnaireItemId == questionnaireItemId &&
                    r.ColumnId == columnId &&
                    r.AssetId == assetId);

            if (existing != null)
            {
                existing.TikVisualID = tikVisualID;
                existing.TikCounter = tikCounter;
                existing.LinkedCaseItemId = linkedCaseItemId;
                return existing;
            }

            var record = new MondayDocumentImport
            {
                CreatedAtUtc = DateTime.UtcNow,
                UpdatedAtUtc = DateTime.UtcNow,
                MondayQuestionnaireItemId = questionnaireItemId,
                LinkedCaseItemId = linkedCaseItemId,
                ColumnId = columnId,
                AssetId = assetId,
                TikVisualID = tikVisualID,
                TikCounter = tikCounter,
                OriginalFileName = fileName,
                Status = DocumentImportStatus.Pending
            };

            _integrationDb.MondayDocumentImports.Add(record);
            var saveSw = Stopwatch.StartNew();
            await _integrationDb.SaveChangesAsync();
            saveSw.Stop();
            if (saveSw.ElapsedMilliseconds > 1000)
            {
                _logger.LogWarning(
                    "SLOW_DB | Worker=DocumentIngestionWorker | Operation=SaveChanges(CreateTrackingRecord) | TikCounter={TikCounter} | TikNumber={TikNumber} | ElapsedMs={ElapsedMs}",
                    tikCounter, tikVisualID, saveSw.ElapsedMilliseconds);
            }
            return record;
        }

        private async Task MarkMissingTikFailure(
            long questionnaireItemId, long linkedCaseItemId, string columnId,
            DocumentIngestionMondayService.FileAssetRef asset, string tikVisualID)
        {
            var assetIdStr = asset.AssetId.ToString();
            var existing = await _integrationDb.MondayDocumentImports
                .FirstOrDefaultAsync(r =>
                    r.MondayQuestionnaireItemId == questionnaireItemId &&
                    r.ColumnId == columnId &&
                    r.AssetId == assetIdStr);

            if (existing != null)
            {
                if (existing.Status == DocumentImportStatus.Success) return;
                existing.Status = DocumentImportStatus.Failed;
                existing.ErrorMessage = $"TikCounter not found in Odcanit for TikVisualID={tikVisualID}";
                existing.UpdatedAtUtc = DateTime.UtcNow;
            }
            else
            {
                var record = new MondayDocumentImport
                {
                    CreatedAtUtc = DateTime.UtcNow,
                    UpdatedAtUtc = DateTime.UtcNow,
                    MondayQuestionnaireItemId = questionnaireItemId,
                    LinkedCaseItemId = linkedCaseItemId,
                    ColumnId = columnId,
                    AssetId = assetIdStr,
                    TikVisualID = tikVisualID,
                    OriginalFileName = asset.Name,
                    Status = DocumentImportStatus.Failed,
                    ErrorMessage = $"TikCounter not found in Odcanit for TikVisualID={tikVisualID}"
                };
                _integrationDb.MondayDocumentImports.Add(record);
            }

            {
                var saveSw = Stopwatch.StartNew();
                await _integrationDb.SaveChangesAsync();
                saveSw.Stop();
                if (saveSw.ElapsedMilliseconds > 1000)
                {
                    _logger.LogWarning(
                        "SLOW_DB | Worker=DocumentIngestionWorker | Operation=SaveChanges(MarkMissingTikFailure) | TikCounter=<null> | TikNumber={TikNumber} | ElapsedMs={ElapsedMs}",
                        tikVisualID, saveSw.ElapsedMilliseconds);
                }
            }

            if (existing == null || !existing.AlertSent)
            {
                SendAlert(
                    $"Missing TikCounter for TikVisualID={tikVisualID}",
                    existing, null);
                if (existing != null)
                {
                    existing.AlertSent = true;
                    var saveSw = Stopwatch.StartNew();
                    await _integrationDb.SaveChangesAsync();
                    saveSw.Stop();
                    if (saveSw.ElapsedMilliseconds > 1000)
                    {
                        _logger.LogWarning(
                            "SLOW_DB | Worker=DocumentIngestionWorker | Operation=SaveChanges(MarkMissingTikFailure.AlertSent) | TikCounter={TikCounter} | TikNumber={TikNumber} | ElapsedMs={ElapsedMs}",
                            existing.TikCounter?.ToString() ?? "<null>", tikVisualID, saveSw.ElapsedMilliseconds);
                    }
                }
            }
        }

        private async Task SaveRecordAsync(MondayDocumentImport record)
        {
            try
            {
                var saveSw = Stopwatch.StartNew();
                await _integrationDb.SaveChangesAsync();
                saveSw.Stop();
                if (saveSw.ElapsedMilliseconds > 1000)
                {
                    _logger.LogWarning(
                        "SLOW_DB | Worker=DocumentIngestionWorker | Operation=SaveChanges(SaveRecord) | TikCounter={TikCounter} | TikNumber={TikNumber} | ElapsedMs={ElapsedMs}",
                        record.TikCounter?.ToString() ?? "<null>", record.TikVisualID ?? "<null>", saveSw.ElapsedMilliseconds);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "DOCINGESTION failed to save tracking record for AssetId={AssetId}", record.AssetId);
            }
        }

        // ───────── Alert ─────────

        private void SendAlert(string subject, MondayDocumentImport? record, Exception? ex)
        {
            var bodyParts = new List<string>
            {
                $"Subject: {subject}",
                $"Time: {DateTime.UtcNow:O}"
            };

            if (record != null)
            {
                bodyParts.Add($"TikVisualID: {record.TikVisualID ?? "N/A"}");
                bodyParts.Add($"TikCounter: {record.TikCounter?.ToString() ?? "N/A"}");
                bodyParts.Add($"MondayItemId: {record.MondayQuestionnaireItemId}");
                bodyParts.Add($"ColumnId: {record.ColumnId}");
                bodyParts.Add($"AssetId: {record.AssetId}");
                bodyParts.Add($"FileName: {record.OriginalFileName}");
                bodyParts.Add($"FileSize: {record.FileSizeBytes}");
                bodyParts.Add($"DocCounter: {record.OdcanitDocCounter?.ToString() ?? "N/A"}");
                bodyParts.Add($"DestPath: {record.OdcanitDestPath ?? "N/A"}");
                bodyParts.Add($"Status: {record.Status}");
                bodyParts.Add($"RetryCount: {record.RetryCount}");
            }

            if (ex != null)
            {
                bodyParts.Add($"Error: {ex.Message}");
                bodyParts.Add($"ExceptionType: {ex.GetType().Name}");
            }

            var body = string.Join(Environment.NewLine, bodyParts);

            _emailNotifier.QueueCriticalAlert(
                $"DocIngestion: {subject}",
                body,
                ex?.GetType().Name,
                "DocumentIngestionService",
                alertType: "Document Ingestion Failure");
        }

        // ───────── Accident Story → Odcanit Nispah (idempotent via CaseAnnexWriteState per TikCounter) ─────────

        internal const string NispahSourceKindAccidentStory = "AccidentStory";
        internal const string NispahSourceKindPdfAsset = "PdfAsset";

        private async Task ProcessAccidentStoryAsync(
            long questionnaireItemId, long linkedCaseItemId,
            string tikVisualID, int tikCounter, CancellationToken ct, string? runId = null)
        {
            var nispahType = _settings.ResolvedNispahType;
            var columnDefs = ResolveAccidentStoryColumnDefs();

            if (columnDefs.Length == 0)
            {
                _logger.LogDebug("ACCIDENTSTORY no columns configured, skipping item {ItemId}", questionnaireItemId);
                return;
            }

            if (!_settings.AccidentStory.WriteEnabled)
            {
                _logger.LogDebug(
                    "ACCIDENTSTORY SKIP (WriteEnabled=false) | TikCounter={TikCounter}, ItemId={ItemId}",
                    tikCounter, questionnaireItemId);
                return;
            }

            // Guard before write: idempotency via per-case state. No Odcanit write or dedup insert if already written.
            try
            {
                if (await IsAccidentStoryAlreadyWrittenAsync(_caseAnnexStateRepo, tikCounter, ct))
                {
                    _logger.LogDebug(
                        "ACCIDENTSTORY already written, skip | TikCounter={TikCounter}, TikVisualID={TikVisualID}",
                        tikCounter, tikVisualID);
                    return;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "ACCIDENTSTORY state read failed | TikCounter={TikCounter}, TikVisualID={TikVisualID}, RunId={RunId}",
                    tikCounter, tikVisualID, runId ?? "");
                return;
            }

            var columnIds = columnDefs.Select(c => c.ColumnId).ToArray();

            Dictionary<string, string> columnValues;
            try
            {
                columnValues = await _mondayService.GetItemColumnValuesAsync(questionnaireItemId, columnIds, ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "ACCIDENTSTORY READ FAILED | TikCounter={TikCounter}, TikVisualID={TikVisualID}, ItemId={ItemId}, LinkedCaseItemId={LinkedCaseItemId}, ColumnCount={ColumnCount}",
                    tikCounter, tikVisualID, questionnaireItemId, linkedCaseItemId, columnIds.Length);
                return;
            }

            // Compose note text ONLY from Monday Q/A bullet lines (no ItemId, timestamp, source header, TikNumber, RunId).
            var result = AccidentStoryComposer.Compose(
                columnDefs, columnValues, questionnaireItemId, DateTime.Now, includeHeader: false);

            if (result == null)
            {
                _accidentStoryEmptyCount++;
                return;
            }

            _logger.LogDebug(
                "ACCIDENTSTORY WRITING | TikCounter={TikCounter}, TikVisualID={TikVisualID}, NispahType={NispahType}, TextLength={TextLength}",
                tikCounter, tikVisualID, nispahType, result.Text.Length);

            var correlationId = $"accidentstory-{questionnaireItemId}";

            try
            {
                var success = await _nispahWriter.CreateNispahAsync(
                    tikVisualID, result.Text, nispahType, correlationId, ct);

                if (success)
                {
                    try
                    {
                        await _caseAnnexStateRepo.MarkAccidentStoryWrittenAsync(tikCounter, runId, ct);
                    }
                    catch (Exception stateEx)
                    {
                        _logger.LogError(stateEx,
                            "ACCIDENTSTORY state update failed | TikCounter={TikCounter}, TikVisualID={TikVisualID}, NispahType={NispahType}, RunId={RunId}, reason=state update failed",
                            tikCounter, tikVisualID, nispahType, runId ?? "");
                        SendAlert(
                            $"Accident story state update failed for TikVisualID={tikVisualID} (repeated writes may occur)",
                            null, stateEx);
                        return;
                    }
                    _logger.LogDebug(
                        "ACCIDENTSTORY SUCCESS | TikCounter={TikCounter}, TikVisualID={TikVisualID}, ItemId={ItemId}",
                        tikCounter, tikVisualID, questionnaireItemId);
                }
                else
                {
                    _logger.LogWarning(
                        "ACCIDENTSTORY BLOCKED | TikCounter={TikCounter}, TikVisualID={TikVisualID}, ItemId={ItemId}, RunId={RunId} — NispahWriter guardrails",
                        tikCounter, tikVisualID, questionnaireItemId, runId ?? "");
                }
            }
            catch (Exception ex)
            {
                // Dedup idempotency: SQL unique violation (2601/2627) = already written in a previous run — mark written and skip, no critical alert.
                if (ex is DbUpdateException dbEx && NispahWriterService.IsSqlUniqueViolation(dbEx))
                {
                    await _caseAnnexStateRepo.MarkAccidentStoryWrittenAsync(tikCounter, runId, ct);
                    _logger.LogWarning(
                        "ACCIDENTSTORY dedup key already exists (skip, marked written) | TikCounter={TikCounter}, TikVisualID={TikVisualID}, NispahType={NispahType}, RunId={RunId}, reason=dedup hit",
                        tikCounter, tikVisualID, nispahType, runId ?? "");
                    return;
                }

                _logger.LogError(ex,
                    "ACCIDENTSTORY FAILED | TikCounter={TikCounter}, TikVisualID={TikVisualID}, ItemId={ItemId}, RunId={RunId}, Error={Error}",
                    tikCounter, tikVisualID, questionnaireItemId, runId ?? "", ex.Message);

                SendAlert(
                    $"Accident story nispah write failed for TikVisualID={tikVisualID}",
                    null, ex);
            }
        }

        /// <summary>True when the exception is a SQL unique constraint violation (2627). For 2601+2627 use NispahWriterService.IsSqlUniqueViolation.</summary>
        internal static bool IsUniqueConstraintViolation(DbUpdateException ex)
        {
            return ex.InnerException is SqlException sqlEx && sqlEx.Number == 2627;
        }

        /// <summary>First 8 characters of SHA256 hex of text (for diagnostic logging). Matches NispahWriterService hash semantics.</summary>
        private static string GetInfoHashPrefix(string text)
        {
            if (string.IsNullOrEmpty(text)) return "";
            using var sha256 = SHA256.Create();
            var hash = sha256.ComputeHash(Encoding.UTF8.GetBytes(text));
            var hex = Convert.ToHexString(hash).ToLowerInvariant();
            return hex.Length >= 8 ? hex[..8] : hex;
        }

        /// <summary>Returns true if the accident story annex is already written for this TikCounter. Used for guard-before-write; exposed for unit tests.</summary>
        internal static async Task<bool> IsAccidentStoryAlreadyWrittenAsync(ICaseAnnexWriteStateRepository repo, int tikCounter, CancellationToken ct = default)
        {
            var state = await repo.GetOrCreateStateAsync(tikCounter, ct);
            return state.AccidentStoryAnnexWritten;
        }

        /// <summary>
        /// Resolves accident story column definitions from new config, with backward compat for old single-column config.
        /// </summary>
        private AccidentStoryColumnDef[] ResolveAccidentStoryColumnDefs()
        {
            if (_settings.AccidentStory.Enabled && _settings.AccidentStory.Columns.Length > 0)
                return _settings.AccidentStory.Columns;

            if (!string.IsNullOrWhiteSpace(_settings.AccidentStoryColumnId))
            {
                return
                [
                    new AccidentStoryColumnDef
                    {
                        ColumnId = _settings.AccidentStoryColumnId,
                        Title = "סיפור תאונה",
                        Type = "long_text",
                        IncludeIfEmpty = false
                    }
                ];
            }

            return [];
        }

        // ───────── Filename normalization (JWT-like / unsafe names) ─────────

        private const int MaxFilenameLength = 120;
        private static readonly char[] InvalidFileNameChars = Path.GetInvalidFileNameChars();

        /// <summary>True if the name should not be used for storage/upload (JWT-like, empty, or unsafe).</summary>
        internal static bool IsSuspiciousFilename(string? name)
        {
            if (string.IsNullOrWhiteSpace(name)) return true;
            var s = name.Trim();
            if (s.Length > MaxFilenameLength) return true;
            if (s.StartsWith("eyJ", StringComparison.OrdinalIgnoreCase)) return true; // JWT header
            var dotCount = 0;
            foreach (var c in s) { if (c == '.') dotCount++; }
            if (dotCount > 4) return true; // many segments (e.g. JWT with .pdf)
            if (s.IndexOfAny(InvalidFileNameChars) >= 0) return true;
            return false;
        }

        /// <summary>Remove invalid chars, trim, cap length. Does not check for JWT/suspicious.</summary>
        internal static string SanitizeFilename(string name, int maxLength = MaxFilenameLength)
        {
            if (string.IsNullOrEmpty(name)) return string.Empty;
            var t = name.Trim();
            var sb = new System.Text.StringBuilder(t.Length);
            foreach (var c in t)
            {
                if (InvalidFileNameChars.Contains(c)) continue;
                sb.Append(c);
            }
            var s = sb.ToString().Trim();
            if (s.Length > maxLength) s = s[..maxLength];
            return s;
        }

        /// <summary>Safe name for storage/upload: Attachment_TikPart_AssetId.ext when suspicious, else sanitized name with resolved extension.</summary>
        internal static string DeriveSafeFilename(string? assetName, string tikVisualID, int tikCounter, long assetId, string extension)
        {
            var ext = extension.TrimStart('.').ToLowerInvariant();
            if (!IsSuspiciousFilename(assetName))
            {
                var sanitized = SanitizeFilename(assetName!);
                if (!string.IsNullOrEmpty(sanitized))
                {
                    var baseName = Path.GetFileNameWithoutExtension(sanitized);
                    if (!string.IsNullOrEmpty(baseName))
                        return baseName.Length > MaxFilenameLength - ext.Length - 1
                            ? baseName[..(MaxFilenameLength - ext.Length - 2)] + "." + ext
                            : baseName + "." + ext;
                }
            }
            var tikPart = SanitizeTikVisualID(tikVisualID);
            if (string.IsNullOrEmpty(tikPart) || tikPart.Length > 60) tikPart = tikCounter.ToString();
            return $"Attachment_{tikPart}_{assetId}.{ext}";
        }

        /// <summary>For logging only: avoid leaking tokens; return length + short prefix.</summary>
        internal static string SafeFileNameForLog(string? name)
        {
            if (string.IsNullOrEmpty(name)) return "len=0";
            const int prefixLen = 8;
            var len = name.Length;
            var prefix = name.Length <= prefixLen ? name : name[..prefixLen] + "...";
            return $"len={len} prefix={prefix}";
        }

        /// <summary>Safe URL diagnostics for logging. Never log the URL or query string.</summary>
        internal static (string UrlHashPrefix, int UrlLength, bool HasAmzSignature) GetDownloadUrlDiagnostics(string? url)
        {
            if (string.IsNullOrEmpty(url))
                return ("", 0, false);
            var bytes = Encoding.UTF8.GetBytes(url);
            var hash = SHA256.HashData(bytes);
            var prefix = Convert.ToHexString(hash.AsSpan(0, 4));
            var hasAmz = url.Contains("X-Amz-Signature", StringComparison.OrdinalIgnoreCase);
            return (prefix, url.Length, hasAmz);
        }

        // ───────── Helpers ─────────

        private static readonly Dictionary<string, string> MimeToExtension = new(StringComparer.OrdinalIgnoreCase)
        {
            ["application/pdf"] = "pdf",
            ["image/jpeg"] = "jpg",
            ["image/jpg"] = "jpg",
            ["image/png"] = "png",
            ["image/gif"] = "gif",
            ["image/webp"] = "webp",
            ["video/mp4"] = "mp4",
            ["video/quicktime"] = "mov",
            ["image/heic"] = "heic",
            ["image/heif"] = "heif",
        };

        internal const int MaxExtensionLength = 5;
        internal const string SourceFileExtension = "file_extension";
        internal const string SourceName = "name";
        internal const string SourceContentType = "content_type";
        internal const string SourceContentDisposition = "content_disposition";
        internal const string SourceMagicBytes = "magic_bytes";

        /// <summary>Result of extension detection with source for logging.</summary>
        internal sealed class ExtensionDetectionResult
        {
            public string Extension { get; init; } = string.Empty;
            public string DetectionSource { get; init; } = string.Empty;
            /// <summary>Set when detection source is content_type.</summary>
            public string? MimeType { get; init; }
        }

        /// <summary>True if the value looks like a token (e.g. from protected_static URL) and should not be used as file type.</summary>
        internal static bool IsTokenLikeExtension(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return true;
            var t = raw.Trim();
            if (t.Length > 10) return true;
            if (t.StartsWith("eyJ", StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        /// <summary>
        /// Strict priority: (a) asset.file_extension if in allowlist, &lt;= 5 chars, and NOT token-like;
        /// (b) asset.name only if NOT JWT-like and Path.GetExtension yields an allowlist extension.
        /// OriginalFileName is never the source of truth when token-like; we fall through to HTTP headers / content-type / magic.
        /// </summary>
        internal static ExtensionDetectionResult? TryDetectExtensionFromMetadata(
            string? assetName,
            string? assetFileExtension,
            IReadOnlyList<string> allowlist)
        {
            if (allowlist == null || allowlist.Count == 0) return null;
            var set = new HashSet<string>(allowlist, StringComparer.OrdinalIgnoreCase);

            static string? Normalize(string? raw)
            {
                if (string.IsNullOrWhiteSpace(raw)) return null;
                var s = raw.Trim().TrimStart('.').ToLowerInvariant();
                return string.IsNullOrEmpty(s) || s.Length > MaxExtensionLength ? null : s;
            }

            static bool IsAllowed(string? ext, HashSet<string> allowed)
            {
                if (string.IsNullOrEmpty(ext) || ext.Length > MaxExtensionLength) return false;
                return allowed.Contains(ext);
            }

            // a) asset.file_extension only if allowlisted and NOT token-like (do not use token from Monday)
            if (!IsTokenLikeExtension(assetFileExtension))
            {
                var apiExt = Normalize(assetFileExtension);
                if (IsAllowed(apiExt, set))
                    return new ExtensionDetectionResult { Extension = apiExt!, DetectionSource = SourceFileExtension };
            }

            // b) asset.name only if NOT JWT-like and Path.GetExtension yields allowlist extension
            if (!string.IsNullOrWhiteSpace(assetName) && !IsSuspiciousFilename(assetName))
            {
                var ext = Normalize(Path.GetExtension(assetName));
                if (IsAllowed(ext, set))
                    return new ExtensionDetectionResult { Extension = ext!, DetectionSource = SourceName };
            }

            return null;
        }

        /// <summary>Try Content-Disposition header filename; only return if extension is in allowlist and ≤5 chars.</summary>
        internal static ExtensionDetectionResult? TryDetectExtensionFromContentDisposition(string? contentDispositionHeader, IReadOnlyList<string> allowlist)
        {
            if (allowlist == null || allowlist.Count == 0 || string.IsNullOrWhiteSpace(contentDispositionHeader)) return null;
            var idx = contentDispositionHeader!.IndexOf("filename", StringComparison.OrdinalIgnoreCase);
            if (idx < 0) return null;
            idx = contentDispositionHeader.IndexOf('=', idx + 8);
            if (idx < 0) return null;
            idx++;
            while (idx < contentDispositionHeader.Length && (contentDispositionHeader[idx] == '*' || contentDispositionHeader[idx] == '=' || char.IsWhiteSpace(contentDispositionHeader[idx])))
                idx++;
            if (idx >= contentDispositionHeader.Length) return null;
            var quote = contentDispositionHeader[idx] == '"' || contentDispositionHeader[idx] == '\'';
            if (quote) idx++;
            var start = idx;
            while (idx < contentDispositionHeader.Length && contentDispositionHeader[idx] != ';' && contentDispositionHeader[idx] != '"' && contentDispositionHeader[idx] != '\'')
                idx++;
            var filename = contentDispositionHeader[start..idx].Trim().Trim('"', '\'');
            if (string.IsNullOrEmpty(filename)) return null;
            var ext = (Path.GetExtension(filename) ?? "").TrimStart('.').ToLowerInvariant();
            if (ext.Length == 0 || ext.Length > MaxExtensionLength) return null;
            var set = new HashSet<string>(allowlist, StringComparer.OrdinalIgnoreCase);
            if (!set.Contains(ext)) return null;
            return new ExtensionDetectionResult { Extension = ext, DetectionSource = SourceContentDisposition };
        }

        /// <summary>Try Content-Type mapping; only return if in allowlist and &lt;= 5 chars.</summary>
        internal static ExtensionDetectionResult? TryDetectExtensionFromContentType(string? contentType, IReadOnlyList<string> allowlist)
        {
            if (allowlist == null || allowlist.Count == 0) return null;
            if (string.IsNullOrWhiteSpace(contentType) || !MimeToExtension.TryGetValue(contentType, out var mapped)) return null;
            var ext = mapped.TrimStart('.').ToLowerInvariant();
            if (ext.Length > MaxExtensionLength) return null;
            var set = new HashSet<string>(allowlist, StringComparer.OrdinalIgnoreCase);
            if (!set.Contains(ext)) return null;
            return new ExtensionDetectionResult { Extension = ext, DetectionSource = SourceContentType, MimeType = contentType };
        }

        private static readonly HashSet<string> HeicBrands = new(StringComparer.Ordinal)
            { "heic", "heix", "mif1", "msf1", "hevc", "hevx" };

        /// <summary>Check first bytes for known signatures; only returns extensions present in the allowlist.</summary>
        internal static ExtensionDetectionResult? TryDetectExtensionFromMagicBytes(byte[] firstBytes, IReadOnlyList<string> allowlist)
        {
            if (allowlist == null || allowlist.Count == 0) return null;
            if (firstBytes == null || firstBytes.Length < 4) return null;
            var set = new HashSet<string>(allowlist, StringComparer.OrdinalIgnoreCase);

            // %PDF (0x25504446)
            if (set.Contains("pdf") &&
                firstBytes[0] == 0x25 && firstBytes[1] == 0x50 && firstBytes[2] == 0x44 && firstBytes[3] == 0x46)
                return new ExtensionDetectionResult { Extension = "pdf", DetectionSource = SourceMagicBytes };

            // ISO BMFF 'ftyp' atom at offset 4
            if (firstBytes.Length >= 8 &&
                firstBytes[4] == 0x66 && firstBytes[5] == 0x74 && firstBytes[6] == 0x79 && firstBytes[7] == 0x70) // ftyp
            {
                // Read major brand at offset 8-11 to distinguish image vs video containers
                if (firstBytes.Length >= 12)
                {
                    var brand = new string(new[] {
                        (char)firstBytes[8], (char)firstBytes[9],
                        (char)firstBytes[10], (char)firstBytes[11] });

                    // HEIC/HEIF image containers — must NOT fall through to mp4
                    if (HeicBrands.Contains(brand))
                    {
                        if (set.Contains("heic"))
                            return new ExtensionDetectionResult { Extension = "heic", DetectionSource = SourceMagicBytes };
                        if (set.Contains("heif"))
                            return new ExtensionDetectionResult { Extension = "heif", DetectionSource = SourceMagicBytes };
                        return null;
                    }

                    // QuickTime container
                    if (brand.StartsWith("qt", StringComparison.Ordinal))
                    {
                        if (set.Contains("mov"))
                            return new ExtensionDetectionResult { Extension = "mov", DetectionSource = SourceMagicBytes };
                        if (set.Contains("qt"))
                            return new ExtensionDetectionResult { Extension = "qt", DetectionSource = SourceMagicBytes };
                    }
                }

                // Generic video container (isom, mp41, mp42, avc1, dash, M4V, etc.)
                if (set.Contains("mp4"))
                    return new ExtensionDetectionResult { Extension = "mp4", DetectionSource = SourceMagicBytes };
                if (set.Contains("mov"))
                    return new ExtensionDetectionResult { Extension = "mov", DetectionSource = SourceMagicBytes };
                if (set.Contains("qt"))
                    return new ExtensionDetectionResult { Extension = "qt", DetectionSource = SourceMagicBytes };
            }

            return null;
        }

        /// <summary>
        /// Returns an extension only if it is in the allowlist and length &lt;= 5.
        /// Tries: file_extension, name (only if valid), URL path, then MIME. Prefer TryDetectExtensionFromMetadata + ContentType + MagicBytes for full flow.
        /// </summary>
        internal static string? GetAllowedExtension(
            string? originalFileName,
            string? assetFileExtension,
            string? assetUrl,
            string? contentType,
            IReadOnlyList<string> allowlist)
        {
            var fromMeta = TryDetectExtensionFromMetadata(originalFileName, assetFileExtension, allowlist);
            if (fromMeta != null) return fromMeta.Extension;
            var set = allowlist != null ? new HashSet<string>(allowlist, StringComparer.OrdinalIgnoreCase) : null;
            if (set != null && !string.IsNullOrWhiteSpace(assetUrl) && Uri.TryCreate(assetUrl, UriKind.Absolute, out var uri))
            {
                var path = uri.AbsolutePath;
                if (!string.IsNullOrEmpty(path))
                {
                    var ext = (Path.GetExtension(path) ?? "").TrimStart('.').ToLowerInvariant();
                    if (ext.Length <= MaxExtensionLength && set.Contains(ext))
                        return ext;
                }
            }
            var fromCt = TryDetectExtensionFromContentType(contentType, allowlist!);
            return fromCt?.Extension;
        }

        /// <summary>Stable safe filename for inbox: asset_{AssetId}.{extension}. Used so file type is never derived from OriginalFileName.</summary>
        internal static string AssetSafeFileName(long assetId, string extension)
        {
            var ext = (extension ?? "").TrimStart('.').ToLowerInvariant();
            if (string.IsNullOrEmpty(ext)) ext = "bin";
            return $"asset_{assetId}.{ext}";
        }

        /// <summary>
        /// Deterministic safe filename: columnSlug_tikSlug_assetId.ext.
        /// Slugify column (whitespace→_, remove invalid chars); TikVisualId: / → -.
        /// </summary>
        internal static string SafeFileName(string columnName, string tikVisualId, long assetId, string extension)
        {
            var invalid = Path.GetInvalidFileNameChars();
            var columnSlug = string.IsNullOrWhiteSpace(columnName)
                ? "col"
                : new string(columnName.Trim().Select(c => char.IsWhiteSpace(c) ? '_' : (invalid.Contains(c) ? '_' : c)).ToArray()).Trim('_');
            if (string.IsNullOrEmpty(columnSlug)) columnSlug = "col";
            var tikSlug = (tikVisualId ?? "").Replace("/", "-").Replace("\\", "-").Trim();
            if (string.IsNullOrEmpty(tikSlug)) tikSlug = "0";
            var ext = (extension ?? "").TrimStart('.').ToLowerInvariant();
            if (string.IsNullOrEmpty(ext)) ext = "bin";
            return $"{columnSlug}_{tikSlug}_{assetId}.{ext}";
        }

        /// <summary>
        /// Deterministic file extension resolution (legacy; prefer GetAllowedExtension for allowlist-safe result).
        /// </summary>
        internal static string ResolveFileExtension(string? assetName, string? assetFileExtension, string? contentType)
        {
            if (!string.IsNullOrWhiteSpace(assetName) && assetName.Contains('.'))
            {
                var ext = (Path.GetExtension(assetName) ?? "").TrimStart('.').ToLowerInvariant();
                if (!string.IsNullOrWhiteSpace(ext) && ext.Length <= 5)
                    return ext;
            }

            if (!string.IsNullOrWhiteSpace(assetFileExtension))
            {
                var ext = assetFileExtension.TrimStart('.').ToLowerInvariant();
                if (!string.IsNullOrWhiteSpace(ext) && ext.Length <= 5)
                    return ext;
            }

            if (!string.IsNullOrWhiteSpace(contentType) && MimeToExtension.TryGetValue(contentType, out var mapped))
                return mapped;

            return "";
        }

        internal static string GenerateSafeFileName(string tikVisualID, long assetId, string columnId, string extension)
        {
            var safeTik = SanitizeTikVisualID(tikVisualID);
            var invalid = Path.GetInvalidFileNameChars();
            var safeCol = new string(columnId.Select(c => invalid.Contains(c) ? '_' : c).ToArray());
            return $"{safeTik}_{assetId}_{safeCol}.{extension}";
        }

        internal static string SanitizeTikVisualID(string tikVisualID)
        {
            return tikVisualID.Replace("/", "_").Replace("\\", "_");
        }
    }

    /// <summary>Thrown when extension cannot be resolved or is denied; do not retry.</summary>
    public sealed class InvalidExtensionException : InvalidOperationException
    {
        public string Reason { get; }
        public string? DetectedExtension { get; }
        public InvalidExtensionException(string message, string reason = "UNSUPPORTED_EXTENSION", string? detectedExtension = null)
            : base(message)
        {
            Reason = reason;
            DetectedExtension = detectedExtension;
        }
    }

    /// <summary>Thrown when file exceeds the configured size limit; do not retry.</summary>
    public sealed class OversizedFileException : InvalidOperationException
    {
        public long FileSize { get; }
        public long MaxSize { get; }
        public OversizedFileException(string message, long fileSize, long maxSize)
            : base(message)
        {
            FileSize = fileSize;
            MaxSize = maxSize;
        }
    }
}
