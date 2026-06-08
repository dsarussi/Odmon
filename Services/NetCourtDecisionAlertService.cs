using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Globalization;
using Odmon.Worker.Configuration;
using Odmon.Worker.Data;
using Odmon.Worker.Models;
using Odmon.Worker.OdcanitAccess;

namespace Odmon.Worker.Services
{
    public class NetCourtDecisionAlertService
    {
        private readonly IntegrationDbContext _integrationDb;
        private readonly INetCourtDocumentReader _documentReader;
        private readonly INetCourtCaseResolver _caseResolver;
        private readonly INetCourtDocumentFileResolver _documentFileResolver;
        private readonly IEmailNotifier _emailNotifier;
        private readonly IConfiguration _configuration;
        private readonly NetCourtDecisionAlertSettings _settings;
        private readonly ILogger<NetCourtDecisionAlertService> _logger;

        public NetCourtDecisionAlertService(
            IntegrationDbContext integrationDb,
            INetCourtDocumentReader documentReader,
            INetCourtCaseResolver caseResolver,
            INetCourtDocumentFileResolver documentFileResolver,
            IEmailNotifier emailNotifier,
            IConfiguration configuration,
            IOptions<NetCourtDecisionAlertSettings> settings,
            ILogger<NetCourtDecisionAlertService> logger)
        {
            _integrationDb = integrationDb;
            _documentReader = documentReader;
            _caseResolver = caseResolver;
            _documentFileResolver = documentFileResolver;
            _emailNotifier = emailNotifier;
            _configuration = configuration;
            _settings = settings.Value;
            _logger = logger;
        }

        public async Task<NetCourtDecisionAlertRunResult> RunAsync(CancellationToken ct)
        {
            var result = new NetCourtDecisionAlertRunResult();
            var startFromDocDate = ParseStartFromDocDate();
            var maxBatchSize = Math.Max(1, _settings.MaxBatchSize);
            var eligibleCandidates = await _documentReader.GetDecisionDocumentsFromDocDateAsync(
                startFromDocDate,
                ct);
            eligibleCandidates = eligibleCandidates
                .Where(x =>
                    IsDecisionDocument(x) &&
                    x.DocDate.HasValue &&
                    x.DocDate.Value.Date >= startFromDocDate)
                .OrderBy(x => x.DocDate)
                .ThenBy(x => x.Counter)
                .ToList();
            result.CandidatesDetected = eligibleCandidates.Count;

            _logger.LogInformation(
                "NETCOURT candidates detected. Count={Count}, StartFromDocDate={StartFromDocDate:yyyy-MM-dd}, MaxBatchSize={MaxBatchSize}",
                eligibleCandidates.Count,
                startFromDocDate,
                maxBatchSize);

            var candidates = await SelectUntrackedBatchAsync(
                eligibleCandidates,
                maxBatchSize,
                result,
                ct);
            if (candidates.Count == 0)
            {
                return result;
            }

            var candidateIdentities = candidates
                .Select(BuildDocumentIdentity)
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            await AddNewTrackingRowsAsync(candidates, result, ct);
            await ProcessBatchRowsAsync(candidateIdentities, result, ct);

            _logger.LogInformation(
                "NETCOURT run complete. EligibleCandidates={Candidates}, NewTracked={NewTracked}, AlreadyProcessed={AlreadyProcessed}, Queued={Queued}, MissingRouting={MissingRouting}, Failed={Failed}",
                result.CandidatesDetected,
                result.NewTracked,
                result.AlreadyProcessed,
                result.EmailsQueued,
                result.MissingRouting,
                result.Failed);

            return result;
        }

        private async Task<List<NetCourtDocument>> SelectUntrackedBatchAsync(
            IReadOnlyCollection<NetCourtDocument> candidates,
            int maxBatchSize,
            NetCourtDecisionAlertRunResult result,
            CancellationToken ct)
        {
            var distinctCandidates = candidates
                .DistinctBy(BuildDocumentIdentity)
                .ToList();
            var identities = distinctCandidates
                .Select(BuildDocumentIdentity)
                .ToArray();
            var existing = new HashSet<string>(StringComparer.Ordinal);

            foreach (var identityChunk in identities.Chunk(1000))
            {
                var tracked = await _integrationDb.NetCourtDecisionAlerts
                    .AsNoTracking()
                    .Where(x => identityChunk.Contains(x.DocumentIdentity))
                    .Select(x => x.DocumentIdentity)
                    .ToListAsync(ct);
                existing.UnionWith(tracked);
            }

            result.AlreadyProcessed += distinctCandidates.Count(
                x => existing.Contains(BuildDocumentIdentity(x)));

            return distinctCandidates
                .Where(x => !existing.Contains(BuildDocumentIdentity(x)))
                .Take(maxBatchSize)
                .ToList();
        }

        private async Task AddNewTrackingRowsAsync(
            IReadOnlyCollection<NetCourtDocument> candidates,
            NetCourtDecisionAlertRunResult result,
            CancellationToken ct)
        {
            var identities = candidates.Select(BuildDocumentIdentity).Distinct().ToArray();
            var existing = identities.Length == 0
                ? new HashSet<string>(StringComparer.Ordinal)
                : (await _integrationDb.NetCourtDecisionAlerts
                    .AsNoTracking()
                    .Where(x => identities.Contains(x.DocumentIdentity))
                    .Select(x => x.DocumentIdentity)
                    .ToListAsync(ct))
                    .ToHashSet(StringComparer.Ordinal);

            foreach (var document in candidates)
            {
                var identity = BuildDocumentIdentity(document);
                if (!existing.Add(identity))
                {
                    result.AlreadyProcessed++;
                    _logger.LogDebug(
                        "NETCOURT already tracked; skipping insert. Identity={Identity}, TikCounter={TikCounter}",
                        identity,
                        document.TikCounter);
                    continue;
                }

                _integrationDb.NetCourtDecisionAlerts.Add(
                    CreateTrackingRow(document, identity, NetCourtDecisionAlertStatuses.Pending));
                result.NewTracked++;
            }

            if (result.NewTracked == 0)
            {
                return;
            }

            try
            {
                await _integrationDb.SaveChangesAsync(ct);
            }
            catch (DbUpdateException ex) when (IsUniqueViolation(ex))
            {
                _logger.LogInformation(ex, "NETCOURT concurrent duplicate tracking insert detected; continuing.");
                _integrationDb.ChangeTracker.Clear();
            }
        }

        private async Task ProcessBatchRowsAsync(
            IReadOnlyCollection<string> candidateIdentities,
            NetCourtDecisionAlertRunResult result,
            CancellationToken ct)
        {
            var rows = await _integrationDb.NetCourtDecisionAlerts
                .Where(x =>
                    candidateIdentities.Contains(x.DocumentIdentity) &&
                    (x.Status == NetCourtDecisionAlertStatuses.Pending ||
                     x.Status == NetCourtDecisionAlertStatuses.ResolutionFailed ||
                     x.Status == NetCourtDecisionAlertStatuses.QueueFailed))
                .OrderBy(x => x.NetCourtCounter)
                .ThenBy(x => x.Id)
                .ToListAsync(ct);

            if (rows.Count == 0)
            {
                return;
            }

            var tikCounters = rows.Select(x => x.TikCounter).Distinct().ToArray();
            var cases = await _caseResolver.GetCasesByTikCountersAsync(tikCounters, ct);
            var casesByCounter = cases
                .GroupBy(x => x.TikCounter)
                .ToDictionary(x => x.Key, x => x.First());

            foreach (var row in rows)
            {
                if (!casesByCounter.TryGetValue(row.TikCounter, out var odcanitCase))
                {
                    row.Status = NetCourtDecisionAlertStatuses.ResolutionFailed;
                    row.ErrorMessage = $"TikCounter {row.TikCounter} was not found in vwExportToOuterSystems_Files.";
                    result.Failed++;
                    _logger.LogWarning(
                        "NETCOURT case resolution failed. Identity={Identity}, TikCounter={TikCounter}",
                        row.DocumentIdentity,
                        row.TikCounter);
                    continue;
                }

                row.TikNumber = odcanitCase.TikNumber;
                row.ClientNumber = DocumentTypeMap.ParseClientNumber(odcanitCase.ClientVisualID, '\\');
                _logger.LogInformation(
                    "NETCOURT case resolved. Identity={Identity}, TikCounter={TikCounter}, TikNumber={TikNumber}, ClientNumber={ClientNumber}",
                    row.DocumentIdentity,
                    row.TikCounter,
                    row.TikNumber,
                    row.ClientNumber);

                if (!TryResolveRecipients(
                        row.ClientNumber,
                        out var intendedRecipient,
                        out var actualRecipients))
                {
                    row.Status = NetCourtDecisionAlertStatuses.MissingRouting;
                    row.IntendedRecipientEmail = null;
                    row.ActualRecipientEmail = null;
                    row.EmailMode = NormalizeEmailMode();
                    row.ErrorMessage = $"No recipient mapping for client number {row.ClientNumber?.ToString() ?? "<null>"}.";
                    result.MissingRouting++;
                    _logger.LogWarning(
                        "NETCOURT missing routing; alert skipped. Identity={Identity}, TikNumber={TikNumber}, ClientNumber={ClientNumber}, FallbackEnabled={FallbackEnabled}",
                        row.DocumentIdentity,
                        row.TikNumber,
                        row.ClientNumber,
                        _settings.FallbackRecipientEnabled);
                    continue;
                }

                row.IntendedRecipientEmail = intendedRecipient;
                row.ActualRecipientEmail = string.Join(";", actualRecipients);
                row.EmailMode = NormalizeEmailMode();
                row.ErrorMessage = null;

                var displayName = ResolveDisplayName(intendedRecipient);
                var subject = $"החלטה חדשה בתיק {row.TikNumber}";
                var body = BuildEmailBody(
                    displayName,
                    row.TikNumber!,
                    _settings.IsTestMode,
                    intendedRecipient);
                var attachments = await ResolveAttachmentAsync(row, ct);

                if (!_emailNotifier.QueueEmail(
                        subject,
                        body,
                        actualRecipients,
                        attachments: attachments))
                {
                    row.Status = NetCourtDecisionAlertStatuses.QueueFailed;
                    row.ErrorMessage = "Email could not be queued.";
                    result.Failed++;
                    _logger.LogWarning(
                        "NETCOURT email queue failed. Identity={Identity}, TikNumber={TikNumber}, ActualRecipients={ActualRecipients}",
                        row.DocumentIdentity,
                        row.TikNumber,
                        row.ActualRecipientEmail);
                    continue;
                }

                row.AlertQueuedAtUtc = DateTime.UtcNow;
                row.Status = _settings.IsTestMode
                    ? NetCourtDecisionAlertStatuses.TestEmailQueued
                    : NetCourtDecisionAlertStatuses.LiveEmailQueued;
                result.EmailsQueued++;

                _logger.LogInformation(
                    attachments.Count > 0
                        ? "NETCOURT email queued with attachment. EmailMode={EmailMode}, Identity={Identity}, TikNumber={TikNumber}, IntendedRecipient={IntendedRecipient}, ActualRecipient={ActualRecipient}, ODDocID={ODDocID}"
                        : "NETCOURT email queued without attachment. EmailMode={EmailMode}, Identity={Identity}, TikNumber={TikNumber}, IntendedRecipient={IntendedRecipient}, ActualRecipient={ActualRecipient}, ODDocID={ODDocID}",
                    NormalizeEmailMode(),
                    row.DocumentIdentity,
                    row.TikNumber,
                    row.IntendedRecipientEmail,
                    row.ActualRecipientEmail,
                    row.ODDocID);
            }

            await _integrationDb.SaveChangesAsync(ct);
        }

        private async Task<IReadOnlyCollection<EmailAttachmentDescriptor>> ResolveAttachmentAsync(
            NetCourtDecisionAlert row,
            CancellationToken ct)
        {
            if (!_settings.AttachDecisionPdf)
            {
                return Array.Empty<EmailAttachmentDescriptor>();
            }

            try
            {
                var resolved = await _documentFileResolver.ResolveAsync(
                    row.ODDocID,
                    row.TikNumber,
                    ct);
                if (!resolved.IsAvailable ||
                    string.IsNullOrWhiteSpace(resolved.FilePath) ||
                    string.IsNullOrWhiteSpace(resolved.FileName))
                {
                    return Array.Empty<EmailAttachmentDescriptor>();
                }

                return new[]
                {
                    new EmailAttachmentDescriptor(
                        resolved.FilePath,
                        resolved.FileName,
                        "application/pdf")
                };
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "NETCOURT attachment skipped because resolution failed unexpectedly. ODDocID={ODDocID}, TikNumber={TikNumber}, Reason={Reason}",
                    row.ODDocID,
                    row.TikNumber,
                    ex.Message);
                return Array.Empty<EmailAttachmentDescriptor>();
            }
        }

        internal bool TryResolveRecipients(
            int? clientNumber,
            out string? intendedRecipient,
            out string[] actualRecipients)
        {
            intendedRecipient = null;
            actualRecipients = Array.Empty<string>();

            if (clientNumber.HasValue &&
                _settings.ClientNumberToRecipientEmail.TryGetValue(clientNumber.Value, out var configured) &&
                !string.IsNullOrWhiteSpace(configured))
            {
                intendedRecipient = configured.Trim();
            }
            else if (_settings.FallbackRecipientEnabled)
            {
                var fallback = _configuration.GetSection("Email:Recipients").Get<string[]>()
                    ?.Where(x => !string.IsNullOrWhiteSpace(x))
                    .Select(x => x.Trim())
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray() ?? Array.Empty<string>();

                if (fallback.Length > 0)
                {
                    intendedRecipient = string.Join(";", fallback);
                    actualRecipients = _settings.IsTestMode
                        ? BuildTestRecipients()
                        : fallback;
                    return actualRecipients.Length > 0;
                }
            }

            if (intendedRecipient == null)
            {
                return false;
            }

            actualRecipients = _settings.IsTestMode
                ? BuildTestRecipients()
                : new[] { intendedRecipient };
            return actualRecipients.Length > 0;
        }

        private string[] BuildTestRecipients()
        {
            if (string.IsNullOrWhiteSpace(_settings.TestRecipient))
            {
                return Array.Empty<string>();
            }

            return new[] { _settings.TestRecipient.Trim() };
        }

        internal static string BuildDocumentIdentity(NetCourtDocument document)
        {
            if (document.CourtDocumentID.HasValue)
                return $"CourtDocumentID:{document.CourtDocumentID.Value}";
            if (document.ODDocID.HasValue)
                return $"ODDocID:{document.ODDocID.Value}";
            if (document.DecisionID.HasValue)
                return $"DecisionID:{document.DecisionID.Value}";
            return $"Counter:{document.Counter}";
        }

        internal static bool IsDecisionDocument(NetCourtDocument document)
            => document.DocType is 2 or 3;

        private DateTime ParseStartFromDocDate()
        {
            if (DateTime.TryParseExact(
                    _settings.StartFromDocDate,
                    "yyyy-MM-dd",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.None,
                    out var startFromDocDate))
            {
                return startFromDocDate.Date;
            }

            throw new InvalidOperationException(
                "NetCourtDecisionAlerts:StartFromDocDate must use yyyy-MM-dd format.");
        }

        internal static string BuildEmailBody(
            string displayName,
            string tikNumber,
            bool isTestMode,
            string? intendedRecipient)
        {
            var body = $"שלום {displayName}{Environment.NewLine}{Environment.NewLine}" +
                       $"התקבלה החלטה חדשה בתיק - {tikNumber}";

            if (isTestMode)
            {
                body += $"{Environment.NewLine}{Environment.NewLine}" +
                        $"מצב בדיקה - המייל המקורי היה מיועד אל: {intendedRecipient}";
            }

            return body;
        }

        internal static string ResolveDisplayName(string? recipient)
        {
            if (string.Equals(recipient, "yonatan@ezer-law.com", StringComparison.OrdinalIgnoreCase))
                return "יונתן";
            if (string.Equals(recipient, "amir@ezer-law.com", StringComparison.OrdinalIgnoreCase))
                return "אמיר";
            return string.Empty;
        }

        private string NormalizeEmailMode()
            => _settings.IsTestMode ? "Test" : "Live";

        private NetCourtDecisionAlert CreateTrackingRow(
            NetCourtDocument document,
            string identity,
            string status)
        {
            return new NetCourtDecisionAlert
            {
                DocumentIdentity = identity,
                TikCounter = document.TikCounter,
                NetCourtCounter = document.Counter,
                ODDocID = document.ODDocID,
                CourtDocumentID = document.CourtDocumentID,
                DecisionID = document.DecisionID,
                DocType = document.DocType,
                Description = Truncate(document.Description, 1000),
                DecisionDesc = Truncate(document.DecisionDesc, 2000),
                DocDate = document.DocDate,
                tsCreateDate = document.tsCreateDate,
                EmailMode = NormalizeEmailMode(),
                Status = status,
                CreatedAtUtc = DateTime.UtcNow
            };
        }

        private static string? Truncate(string? value, int maxLength)
            => value != null && value.Length > maxLength ? value[..maxLength] : value;

        private static bool IsUniqueViolation(DbUpdateException ex)
        {
            var sqlException = ex.InnerException as SqlException
                ?? ex.GetBaseException() as SqlException;
            return sqlException?.Number is 2601 or 2627;
        }
    }

    public class NetCourtDecisionAlertRunResult
    {
        public int CandidatesDetected { get; set; }
        public int NewTracked { get; set; }
        public int AlreadyProcessed { get; set; }
        public int EmailsQueued { get; set; }
        public int MissingRouting { get; set; }
        public int Failed { get; set; }
    }
}
