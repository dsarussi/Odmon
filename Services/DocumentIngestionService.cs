using System.Diagnostics;
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
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly IEmailNotifier _emailNotifier;
        private readonly DocumentIngestionSettings _settings;
        private readonly ILogger<DocumentIngestionService> _logger;

        private int _totalProcessed;
        private int _totalSucceeded;
        private int _totalFailed;
        private int _totalSkipped;

        private static volatile bool _nispahDedupTableMissing;

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

            _logger.LogInformation(
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
                    item.ItemId, linkedCaseItemId, tikVisualID, tikCounter.Value, ct);
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

            _logger.LogInformation(
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
                    currentStage = "UPLOAD";
                    _logger.LogInformation(
                        "DOCINGESTION STAGE=UPLOAD | AssetId={AssetId}, ItemId={ItemId}, TikCounter={TikCounter}, TikVisualID={TikVisualID}, {Attempt}",
                        assetIdStr, questionnaireItemId, tikCounter, tikVisualID, attemptLabel);
                    await CreateDocumentRowAsync(record, tikCounter, ct);
                    await CopyToDestPathAsync(record, ct);
                    _logger.LogInformation(
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
                    _logger.LogInformation(
                        "DOCINGESTION STAGE=UPLOAD | AssetId={AssetId}, ItemId={ItemId}, TikCounter={TikCounter}, {Attempt}",
                        assetIdStr, questionnaireItemId, tikCounter, attemptLabel);
                    await CopyToDestPathAsync(record, ct);
                    _logger.LogInformation(
                        "DOCINGESTION STAGE=UPLOAD SUCCESS | AssetId={AssetId}, ItemId={ItemId}, TikCounter={TikCounter}, DestPath={DestPath}",
                        assetIdStr, questionnaireItemId, tikCounter, record.OdcanitDestPath);
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
                    "DOCINGESTION SUCCESS | TikVisualID={TikVisualID}, TikCounter={TikCounter}, ItemId={ItemId}, ColId={ColId}, AssetId={AssetId}, OriginalFileNameLog={OriginalFileNameLog}, SafeFile={SafeFile}, DocCounter={DocCounter}, DestPath={DestPath}, Elapsed={ElapsedMs}ms",
                    tikVisualID, tikCounter, questionnaireItemId, columnId, assetIdStr,
                    SafeFileNameForLog(record.OriginalFileName), Path.GetFileName(record.InboxFilePath), record.OdcanitDocCounter, record.OdcanitDestPath, assetSw.ElapsedMilliseconds);
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
                    "DOCINGESTION FAILED | STAGE={Stage}, AssetId={AssetId}, ItemId={ItemId}, TikCounter={TikCounter}, TikVisualID={TikVisualID}, {Attempt}, Error={Error}, Elapsed={ElapsedMs}ms",
                    currentStage, assetIdStr, questionnaireItemId, tikCounter, tikVisualID, attemptLabel, ex.Message, assetSw.ElapsedMilliseconds);

                if (!record.AlertSent)
                {
                    SendAlert(
                        $"Document ingestion failed: AssetId={assetIdStr}, TikCounter={tikCounter}, Stage={currentStage}",
                        record, ex);
                    record.AlertSent = true;
                    await SaveRecordAsync(record);
                }
            }
        }

        // ───────── Step 1: Download ─────────

        private async Task DownloadAssetToInboxAsync(
            MondayDocumentImport record, long assetId, string tikVisualID, int tikCounter, CancellationToken ct,
            int attempt, int maxAttempts)
        {
            var dlSw = Stopwatch.StartNew();
            var attemptLabel = $"Attempt {attempt}/{maxAttempts}";

            _logger.LogInformation(
                "DOCINGESTION STAGE=DOWNLOAD | AssetId={AssetId}, ItemId={ItemId}, TikCounter={TikCounter}, TikVisualID={TikVisualID}, {Attempt}",
                assetId, record.MondayQuestionnaireItemId, tikCounter, tikVisualID, attemptLabel);

            var assetInfo = await _mondayService.GetAssetDownloadInfoAsync(assetId, ct);
            if (assetInfo == null || string.IsNullOrWhiteSpace(assetInfo.PublicUrl))
                throw new InvalidOperationException($"Cannot obtain download URL for asset {assetId}");

            record.OriginalFileName = assetInfo.Name ?? string.Empty;

            if (assetInfo.FileSize > _settings.MaxFileSizeBytes)
                throw new InvalidOperationException(
                    $"Asset {assetId} size {assetInfo.FileSize} exceeds max {_settings.MaxFileSizeBytes} bytes");

            var ext = ResolveFileExtension(assetInfo.Name, assetInfo.FileExtension, contentType: null);

            using var downloadClient = _httpClientFactory.CreateClient("MondayFileDownload");
            HttpResponseMessage? response = null;
            try
            {
                response = await downloadClient.GetAsync(assetInfo.PublicUrl, HttpCompletionOption.ResponseHeadersRead, ct);
                response.EnsureSuccessStatusCode();
            }
            catch (HttpRequestException ex)
            {
                var statusCode = response?.StatusCode;
                _logger.LogWarning(ex,
                    "DOCINGESTION STAGE=DOWNLOAD FAILED | AssetId={AssetId}, ItemId={ItemId}, TikCounter={TikCounter}, {Attempt}, StatusCode={StatusCode}, Error={Error}",
                    assetId, record.MondayQuestionnaireItemId, tikCounter, attemptLabel, (int?)statusCode ?? 0, ex.Message);
                throw;
            }

            if (string.IsNullOrWhiteSpace(ext))
            {
                var contentType = response!.Content.Headers.ContentType?.MediaType;
                ext = ResolveFileExtension(null, null, contentType);
                if (!string.IsNullOrWhiteSpace(ext))
                    _logger.LogInformation(
                        "DOCINGESTION EXT RESOLVED VIA MIME | AssetId={AssetId}, ContentType={ContentType}, Ext={Ext}, OriginalFileNameLog={OriginalFileNameLog}",
                        assetId, contentType, ext, SafeFileNameForLog(assetInfo.Name));
            }

            if (string.IsNullOrWhiteSpace(ext))
                throw new InvalidOperationException(
                    $"Cannot determine file extension for asset {assetId}, FileExtension='{assetInfo.FileExtension}', ContentType='{response.Content.Headers.ContentType?.MediaType}'");

            var allowedSet = new HashSet<string>(_settings.AllowedExtensions, StringComparer.OrdinalIgnoreCase);
            if (!allowedSet.Contains(ext))
                throw new InvalidOperationException(
                    $"File extension '{ext}' not in allowlist [{string.Join(",", _settings.AllowedExtensions)}] for asset {assetId}");

            var safeTikDir = SanitizeTikVisualID(tikVisualID);
            var caseFolderPath = Path.Combine(_settings.InboxPath, "Cases", safeTikDir);
            Directory.CreateDirectory(caseFolderPath);

            var safeFileName = DeriveSafeFilename(assetInfo.Name, tikVisualID, tikCounter, assetId, ext);
            var targetPath = Path.Combine(caseFolderPath, safeFileName);
            var tempPath = targetPath + ".tmp";

            await using (var contentStream = await response.Content.ReadAsStreamAsync(ct))
            await using (var fileStream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920))
            {
                await contentStream.CopyToAsync(fileStream, ct);
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
            await SaveRecordAsync(record);

            dlSw.Stop();
            _logger.LogInformation(
                "DOCINGESTION STAGE=DOWNLOAD SUCCESS | AssetId={AssetId}, ItemId={ItemId}, TikCounter={TikCounter}, SafeFile={SafeFileName}, Ext={Ext}, Size={Size}, OriginalFileNameLog={OriginalFileNameLog}, Elapsed={ElapsedMs}ms",
                assetId, record.MondayQuestionnaireItemId, tikCounter, safeFileName, ext, fileInfo.Length, SafeFileNameForLog(assetInfo.Name), dlSw.ElapsedMilliseconds);
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

        // ───────── Accident Story → Odcanit Nispah (multi-column) ─────────

        private async Task ProcessAccidentStoryAsync(
            long questionnaireItemId, long linkedCaseItemId,
            string tikVisualID, int tikCounter, CancellationToken ct)
        {
            var nispahType = _settings.ResolvedNispahType;
            var columnDefs = ResolveAccidentStoryColumnDefs();

            if (columnDefs.Length == 0)
            {
                _logger.LogDebug("ACCIDENTSTORY no columns configured, skipping item {ItemId}", questionnaireItemId);
                return;
            }

            var columnIds = columnDefs.Select(c => c.ColumnId).ToArray();

            // Step 1: Read all column values in one API call
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

            // Step 2: Compose the text block
            var result = AccidentStoryComposer.Compose(
                columnDefs, columnValues, questionnaireItemId, DateTime.Now);

            if (result == null)
            {
                _logger.LogInformation(
                    "ACCIDENTSTORY SKIP (empty) | TikCounter={TikCounter}, TikVisualID={TikVisualID}, ItemId={ItemId}, LinkedCaseItemId={LinkedCaseItemId} — all column values empty",
                    tikCounter, tikVisualID, questionnaireItemId, linkedCaseItemId);
                return;
            }

            _logger.LogInformation(
                "ACCIDENTSTORY COMPOSED | TikCounter={TikCounter}, TikVisualID={TikVisualID}, ItemId={ItemId}, LinkedCaseItemId={LinkedCaseItemId}, LinesIncluded={LinesIncluded}, TextLength={TextLength}, ContentHash={ContentHash}",
                tikCounter, tikVisualID, questionnaireItemId, linkedCaseItemId,
                result.LinesIncluded, result.Text.Length, result.ContentHash);

            // Step 3: Permanent dedup — check if this exact content was already written
            bool alreadyWritten = false;
            if (!_nispahDedupTableMissing)
            {
                try
                {
                    alreadyWritten = await _integrationDb.NispahDeduplications
                        .AsNoTracking()
                        .AnyAsync(d =>
                            d.TikVisualID == tikVisualID &&
                            d.NispahTypeName == nispahType &&
                            d.InfoHash == result.ContentHash, ct);
                }
                catch (SqlException sqlEx) when (sqlEx.Number == 208)
                {
                    if (!_nispahDedupTableMissing)
                    {
                        _nispahDedupTableMissing = true;
                        _logger.LogWarning(
                            "NispahDeduplications table does not exist (SqlException 208). Dedup bypassed for accident story — ingestion will continue. Run EF migrations to create the table.");
                    }
                }
            }

            if (alreadyWritten)
            {
                _logger.LogInformation(
                    "ACCIDENTSTORY SKIP (duplicate) | TikCounter={TikCounter}, TikVisualID={TikVisualID}, ItemId={ItemId}, LinkedCaseItemId={LinkedCaseItemId}, ContentHash={ContentHash}",
                    tikCounter, tikVisualID, questionnaireItemId, linkedCaseItemId, result.ContentHash);
                return;
            }

            // Step 4: Write to Odcanit via NispahWriterService
            var correlationId = $"accidentstory-{questionnaireItemId}";

            _logger.LogInformation(
                "ACCIDENTSTORY WRITING | TikCounter={TikCounter}, TikVisualID={TikVisualID}, ItemId={ItemId}, NispahType='{NispahType}', TextLength={TextLength}",
                tikCounter, tikVisualID, questionnaireItemId, nispahType, result.Text.Length);

            try
            {
                var success = await _nispahWriter.CreateNispahAsync(
                    tikVisualID, result.Text, nispahType, correlationId, ct);

                if (success)
                {
                    _logger.LogInformation(
                        "ACCIDENTSTORY SUCCESS | TikCounter={TikCounter}, TikVisualID={TikVisualID}, ItemId={ItemId}, LinkedCaseItemId={LinkedCaseItemId}, NispahType='{NispahType}', LinesIncluded={LinesIncluded}, TextLength={TextLength}",
                        tikCounter, tikVisualID, questionnaireItemId, linkedCaseItemId, nispahType, result.LinesIncluded, result.Text.Length);
                }
                else
                {
                    _logger.LogWarning(
                        "ACCIDENTSTORY BLOCKED | TikCounter={TikCounter}, TikVisualID={TikVisualID}, ItemId={ItemId}, NispahType='{NispahType}' — blocked by NispahWriter guardrails",
                        tikCounter, tikVisualID, questionnaireItemId, nispahType);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "ACCIDENTSTORY FAILED | TikCounter={TikCounter}, TikVisualID={TikVisualID}, ItemId={ItemId}, LinkedCaseItemId={LinkedCaseItemId}, NispahType='{NispahType}', TextLength={TextLength}, Error={Error}",
                    tikCounter, tikVisualID, questionnaireItemId, linkedCaseItemId, nispahType, result.Text.Length, ex.Message);

                SendAlert(
                    $"Accident story nispah write failed for TikVisualID={tikVisualID}",
                    null, ex);
            }
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

        // ───────── Helpers ─────────

        private static readonly Dictionary<string, string> MimeToExtension = new(StringComparer.OrdinalIgnoreCase)
        {
            ["application/pdf"] = "pdf",
            ["image/jpeg"] = "jpg",
            ["image/jpg"] = "jpg",
            ["image/png"] = "png",
            ["image/gif"] = "gif",
            ["image/webp"] = "webp"
        };

        /// <summary>
        /// Deterministic file extension resolution:
        /// a) assetName if it contains '.' → Path.GetExtension (last segment)
        /// b) assetFileExtension field from Monday API
        /// c) MIME type mapping from Content-Type header
        /// </summary>
        internal static string ResolveFileExtension(string? assetName, string? assetFileExtension, string? contentType)
        {
            if (!string.IsNullOrWhiteSpace(assetName) && assetName.Contains('.'))
            {
                var ext = (Path.GetExtension(assetName) ?? "").TrimStart('.').ToLowerInvariant();
                if (!string.IsNullOrWhiteSpace(ext))
                    return ext;
            }

            if (!string.IsNullOrWhiteSpace(assetFileExtension))
            {
                var ext = assetFileExtension.TrimStart('.').ToLowerInvariant();
                if (!string.IsNullOrWhiteSpace(ext))
                    return ext;
            }

            if (!string.IsNullOrWhiteSpace(contentType) && MimeToExtension.TryGetValue(contentType, out var mapped))
                return mapped;

            return "";
        }

        [Obsolete("Use ResolveFileExtension instead")]
        internal static string ExtractFileExtension(string originalFileName, string? fallbackExtension)
            => ResolveFileExtension(originalFileName, fallbackExtension, contentType: null);

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
