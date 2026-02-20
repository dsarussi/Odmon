using System.Diagnostics;
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
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly IEmailNotifier _emailNotifier;
        private readonly DocumentIngestionSettings _settings;
        private readonly ILogger<DocumentIngestionService> _logger;

        private int _totalProcessed;
        private int _totalSucceeded;
        private int _totalFailed;
        private int _totalSkipped;

        public DocumentIngestionService(
            IntegrationDbContext integrationDb,
            DocumentIngestionMondayService mondayService,
            OdcanitDocumentWriter documentWriter,
            NispahWriterService nispahWriter,
            IHttpClientFactory httpClientFactory,
            IEmailNotifier emailNotifier,
            IOptions<DocumentIngestionSettings> settings,
            ILogger<DocumentIngestionService> logger)
        {
            _integrationDb = integrationDb;
            _mondayService = mondayService;
            _documentWriter = documentWriter;
            _nispahWriter = nispahWriter;
            _httpClientFactory = httpClientFactory;
            _emailNotifier = emailNotifier;
            _settings = settings.Value;
            _logger = logger;
        }

        public async Task RunIngestionAsync(CancellationToken ct)
        {
            _totalProcessed = 0;
            _totalSucceeded = 0;
            _totalFailed = 0;
            _totalSkipped = 0;

            var sw = Stopwatch.StartNew();
            _logger.LogInformation("DOCINGESTION RUN START | Board={BoardId}", _settings.BoardId);

            List<DocumentIngestionMondayService.QuestionnaireItem> items;
            try
            {
                items = await _mondayService.FetchQuestionnaireItemsAsync(
                    _settings.BoardId, _settings.Columns, _settings.RelationColumnId,
                    _settings.ItemsPageLimit, ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "DOCINGESTION failed to fetch questionnaire items from board {BoardId}", _settings.BoardId);
                SendAlert("Failed to fetch questionnaire board", null, ex);
                return;
            }

            foreach (var item in items)
            {
                if (ct.IsCancellationRequested) break;

                try
                {
                    await ProcessQuestionnaireItemAsync(item, ct);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "DOCINGESTION unhandled error processing questionnaire item {ItemId}", item.ItemId);
                }
            }

            sw.Stop();
            _logger.LogInformation(
                "DOCINGESTION RUN COMPLETE | Elapsed={ElapsedMs}ms, Processed={Processed}, Succeeded={Succeeded}, Failed={Failed}, Skipped={Skipped}",
                sw.ElapsedMilliseconds, _totalProcessed, _totalSucceeded, _totalFailed, _totalSkipped);
        }

        private async Task ProcessQuestionnaireItemAsync(
            DocumentIngestionMondayService.QuestionnaireItem item, CancellationToken ct)
        {
            if (item.FileColumns.Count == 0)
            {
                _logger.LogDebug("DOCINGESTION item {ItemId} has no file columns with assets, skipping", item.ItemId);
                return;
            }

            if (item.LinkedCaseItemIds.Count == 0)
            {
                _logger.LogWarning("DOCINGESTION item {ItemId} ('{Name}') has no linked case item, skipping all files",
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

            _logger.LogInformation(
                "DOCINGESTION resolved TikVisualID={TikVisualID} → TikCounter={TikCounter} for item {ItemId}",
                tikVisualID, tikCounter.Value, item.ItemId);

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

            if (!string.IsNullOrWhiteSpace(_settings.AccidentStoryColumnId))
            {
                await ProcessAccidentStoryAsync(
                    item.ItemId, tikVisualID, tikCounter.Value, ct);
            }
        }

        private async Task ProcessSingleAssetAsync(
            long questionnaireItemId, long linkedCaseItemId, string columnId,
            DocumentIngestionMondayService.FileAssetRef assetRef,
            string tikVisualID, int tikCounter, CancellationToken ct)
        {
            _totalProcessed++;
            var assetIdStr = assetRef.AssetId.ToString();
            var assetSw = Stopwatch.StartNew();

            var record = await GetOrCreateTrackingRecordAsync(
                questionnaireItemId, columnId, assetIdStr,
                tikVisualID, tikCounter, linkedCaseItemId, assetRef.Name);

            if (record.Status == DocumentImportStatus.Success)
            {
                _logger.LogDebug("DOCINGESTION asset {AssetId} already imported (Success), skipping", assetIdStr);
                _totalSkipped++;
                return;
            }

            if (record.Status == DocumentImportStatus.Failed && record.RetryCount >= _settings.MaxRetryCount)
            {
                _logger.LogWarning("DOCINGESTION asset {AssetId} exceeded max retries ({Max}), skipping",
                    assetIdStr, _settings.MaxRetryCount);
                _totalSkipped++;
                return;
            }

            record.LastAttemptAtUtc = DateTime.UtcNow;
            if (record.Status == DocumentImportStatus.Failed)
                record.RetryCount++;

            _logger.LogInformation(
                "DOCINGESTION START | TikVisualID={TikVisualID}, TikCounter={TikCounter}, ItemId={ItemId}, ColId={ColId}, AssetId={AssetId}, File={FileName}, Retry={Retry}",
                tikVisualID, tikCounter, questionnaireItemId, columnId, assetIdStr, assetRef.Name, record.RetryCount);

            try
            {
                // Step 1: Download (skip if already downloaded and file exists)
                if (record.Status < DocumentImportStatus.Downloaded ||
                    string.IsNullOrEmpty(record.InboxFilePath) ||
                    !File.Exists(record.InboxFilePath))
                {
                    await DownloadAssetToInboxAsync(record, assetRef.AssetId, tikVisualID, tikCounter, ct);
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
                    await CreateDocumentRowAsync(record, tikCounter, ct);
                }
                else
                {
                    _logger.LogDebug("DOCINGESTION asset {AssetId} SP already called, DocCounter={DocCounter}",
                        assetIdStr, record.OdcanitDocCounter);
                }

                // Step 3: Copy to DestPath
                if (record.Status < DocumentImportStatus.Copied)
                {
                    await CopyToDestPathAsync(record, ct);
                }

                // Step 4: Verify
                if (record.Status < DocumentImportStatus.Verified)
                {
                    VerifyCopy(record);
                }

                // Step 5: Clean up inbox file
                CleanupInboxFile(record);

                // Mark success
                record.Status = DocumentImportStatus.Success;
                record.ErrorMessage = null;
                record.UpdatedAtUtc = DateTime.UtcNow;
                await SaveRecordAsync(record);

                assetSw.Stop();
                _totalSucceeded++;
                _logger.LogInformation(
                    "DOCINGESTION SUCCESS | TikVisualID={TikVisualID}, TikCounter={TikCounter}, ItemId={ItemId}, ColId={ColId}, AssetId={AssetId}, File={FileName}, DocCounter={DocCounter}, DestPath={DestPath}, Elapsed={ElapsedMs}ms",
                    tikVisualID, tikCounter, questionnaireItemId, columnId, assetIdStr,
                    record.OriginalFileName, record.OdcanitDocCounter, record.OdcanitDestPath, assetSw.ElapsedMilliseconds);
            }
            catch (Exception ex)
            {
                assetSw.Stop();
                _totalFailed++;
                record.Status = DocumentImportStatus.Failed;
                record.ErrorMessage = ex.Message.Length > 2000 ? ex.Message[..2000] : ex.Message;
                record.UpdatedAtUtc = DateTime.UtcNow;
                await SaveRecordAsync(record);

                _logger.LogError(ex,
                    "DOCINGESTION FAILED | TikVisualID={TikVisualID}, TikCounter={TikCounter}, ItemId={ItemId}, ColId={ColId}, AssetId={AssetId}, File={FileName}, LastStatus={Status}, Elapsed={ElapsedMs}ms",
                    tikVisualID, tikCounter, questionnaireItemId, columnId, assetIdStr,
                    record.OriginalFileName, record.Status, assetSw.ElapsedMilliseconds);

                if (!record.AlertSent)
                {
                    SendAlert(
                        $"Document ingestion failed: {record.OriginalFileName}",
                        record, ex);
                    record.AlertSent = true;
                    await SaveRecordAsync(record);
                }
            }
        }

        // ───────── Step 1: Download ─────────

        private async Task DownloadAssetToInboxAsync(
            MondayDocumentImport record, long assetId, string tikVisualID, int tikCounter, CancellationToken ct)
        {
            var dlSw = Stopwatch.StartNew();

            var assetInfo = await _mondayService.GetAssetDownloadInfoAsync(assetId, ct);
            if (assetInfo == null || string.IsNullOrWhiteSpace(assetInfo.PublicUrl))
                throw new InvalidOperationException($"Cannot obtain download URL for asset {assetId}");

            record.OriginalFileName = assetInfo.Name;

            var ext = ExtractFileExtension(assetInfo.Name, assetInfo.FileExtension);
            if (string.IsNullOrWhiteSpace(ext))
                throw new InvalidOperationException(
                    $"Cannot determine file extension for asset {assetId}, OriginalFileName='{assetInfo.Name}'");

            var allowedSet = new HashSet<string>(_settings.AllowedExtensions, StringComparer.OrdinalIgnoreCase);
            if (!allowedSet.Contains(ext))
                throw new InvalidOperationException(
                    $"File extension '{ext}' not in allowlist [{string.Join(",", _settings.AllowedExtensions)}] for asset {assetId}, OriginalFileName='{assetInfo.Name}'");

            if (assetInfo.FileSize > _settings.MaxFileSizeBytes)
                throw new InvalidOperationException(
                    $"Asset {assetId} size {assetInfo.FileSize} exceeds max {_settings.MaxFileSizeBytes} bytes");

            var safeTikDir = SanitizeTikVisualID(tikVisualID);
            var caseFolderPath = Path.Combine(_settings.InboxPath, "Cases", safeTikDir);
            Directory.CreateDirectory(caseFolderPath);

            var safeFileName = GenerateSafeFileName(tikVisualID, assetId, record.ColumnId, ext);
            var targetPath = Path.Combine(caseFolderPath, safeFileName);
            var tempPath = targetPath + ".tmp";

            using var downloadClient = _httpClientFactory.CreateClient("MondayFileDownload");
            using var response = await downloadClient.GetAsync(assetInfo.PublicUrl, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();

            await using (var contentStream = await response.Content.ReadAsStreamAsync(ct))
            await using (var fileStream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920))
            {
                await contentStream.CopyToAsync(fileStream, ct);
            }

            if (File.Exists(targetPath))
                File.Delete(targetPath);
            File.Move(tempPath, targetPath);

            var fileInfo = new FileInfo(targetPath);
            record.InboxFilePath = targetPath;
            record.FileSizeBytes = fileInfo.Length;
            record.Status = DocumentImportStatus.Downloaded;
            record.UpdatedAtUtc = DateTime.UtcNow;
            await SaveRecordAsync(record);

            dlSw.Stop();
            _logger.LogInformation(
                "DOCINGESTION DOWNLOADED | AssetId={AssetId}, OriginalFile={OriginalFileName}, SafeFile={SafeFileName}, Ext={Ext}, Size={Size}, InboxPath={InboxPath}, Elapsed={ElapsedMs}ms",
                assetId, assetInfo.Name, safeFileName, ext, fileInfo.Length, targetPath, dlSw.ElapsedMilliseconds);
        }

        // ───────── Step 2: Create Odcanit document row ─────────

        private async Task CreateDocumentRowAsync(
            MondayDocumentImport record, int tikCounter, CancellationToken ct)
        {
            var spSw = Stopwatch.StartNew();

            var safeDocName = Path.GetFileName(record.InboxFilePath!);
            var result = await _documentWriter.CreateDocumentRowAsync(
                tikCounter, safeDocName, record.InboxFilePath!, ct);

            record.OdcanitDocCounter = result.DocCounter;
            record.OdcanitDestPath = result.DestPath;
            record.Status = DocumentImportStatus.SpCreated;
            record.UpdatedAtUtc = DateTime.UtcNow;
            await SaveRecordAsync(record);

            spSw.Stop();
            _logger.LogInformation(
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
            await SaveRecordAsync(record);

            copySw.Stop();
            _logger.LogInformation(
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

            _logger.LogInformation(
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
                    _logger.LogInformation("DOCINGESTION DELETED inbox file | AssetId={AssetId}, Path={Path}",
                        record.AssetId, record.InboxFilePath);
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
            await _integrationDb.SaveChangesAsync();
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

            await _integrationDb.SaveChangesAsync();

            if (existing == null || !existing.AlertSent)
            {
                SendAlert(
                    $"Missing TikCounter for TikVisualID={tikVisualID}",
                    existing, null);
                if (existing != null)
                {
                    existing.AlertSent = true;
                    await _integrationDb.SaveChangesAsync();
                }
            }
        }

        private async Task SaveRecordAsync(MondayDocumentImport record)
        {
            try
            {
                await _integrationDb.SaveChangesAsync();
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
                "DocumentIngestionService");
        }

        // ───────── Accident Story → Odcanit Nispah ─────────

        private async Task ProcessAccidentStoryAsync(
            long questionnaireItemId, string tikVisualID, int tikCounter, CancellationToken ct)
        {
            var columnId = _settings.AccidentStoryColumnId!;
            string? storyText;

            try
            {
                storyText = await _mondayService.GetItemColumnTextAsync(questionnaireItemId, columnId, ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "ACCIDENTSTORY READ FAILED | TikCounter={TikCounter}, TikVisualID={TikVisualID}, ItemId={ItemId}, ColumnId={ColumnId}",
                    tikCounter, tikVisualID, questionnaireItemId, columnId);
                return;
            }

            if (string.IsNullOrWhiteSpace(storyText))
            {
                _logger.LogDebug(
                    "ACCIDENTSTORY EMPTY | TikCounter={TikCounter}, TikVisualID={TikVisualID}, ItemId={ItemId}, ColumnId={ColumnId}",
                    tikCounter, tikVisualID, questionnaireItemId, columnId);
                return;
            }

            var preview = storyText.Length <= 20 ? storyText : storyText[..20] + "…";
            _logger.LogInformation(
                "ACCIDENTSTORY READ | TikCounter={TikCounter}, TikVisualID={TikVisualID}, ItemId={ItemId}, ColumnId={ColumnId}, TextLength={TextLength}, Preview='{Preview}'",
                tikCounter, tikVisualID, questionnaireItemId, columnId, storyText.Length, preview);

            var nispahType = _settings.AccidentStoryNispahType;
            var correlationId = $"docingestion-{questionnaireItemId}-{columnId}";

            _logger.LogInformation(
                "ACCIDENTSTORY WRITING | TikCounter={TikCounter}, TikVisualID={TikVisualID}, ItemId={ItemId}, ColumnId={ColumnId}, NispahType='{NispahType}', TextLength={TextLength}",
                tikCounter, tikVisualID, questionnaireItemId, columnId, nispahType, storyText.Length);

            try
            {
                var success = await _nispahWriter.CreateNispahAsync(
                    tikVisualID, storyText, nispahType, correlationId, ct);

                if (success)
                {
                    _logger.LogInformation(
                        "ACCIDENTSTORY SUCCESS | TikCounter={TikCounter}, TikVisualID={TikVisualID}, ItemId={ItemId}, ColumnId={ColumnId}, NispahType='{NispahType}', TextLength={TextLength}",
                        tikCounter, tikVisualID, questionnaireItemId, columnId, nispahType, storyText.Length);
                }
                else
                {
                    _logger.LogWarning(
                        "ACCIDENTSTORY BLOCKED | TikCounter={TikCounter}, TikVisualID={TikVisualID}, ItemId={ItemId}, ColumnId={ColumnId}, NispahType='{NispahType}' — blocked by NispahWriter guardrails (dedup/rate-limit/validation)",
                        tikCounter, tikVisualID, questionnaireItemId, columnId, nispahType);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "ACCIDENTSTORY WRITE FAILED | TikCounter={TikCounter}, TikVisualID={TikVisualID}, ItemId={ItemId}, ColumnId={ColumnId}, NispahType='{NispahType}', TextLength={TextLength}",
                    tikCounter, tikVisualID, questionnaireItemId, columnId, nispahType, storyText.Length);

                SendAlert(
                    $"Accident story nispah write failed for TikVisualID={tikVisualID}",
                    null, ex);
            }
        }

        // ───────── Helpers ─────────

        internal static string ExtractFileExtension(string originalFileName, string? fallbackExtension)
        {
            var ext = (Path.GetExtension(originalFileName) ?? "").TrimStart('.').ToLowerInvariant();
            if (string.IsNullOrWhiteSpace(ext) && !string.IsNullOrWhiteSpace(fallbackExtension))
                ext = fallbackExtension.TrimStart('.').ToLowerInvariant();
            return ext;
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
}
