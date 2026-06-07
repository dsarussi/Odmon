using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Odmon.Worker.Configuration;
using Odmon.Worker.Data;
using Odmon.Worker.Models;
using Odmon.Worker.OdcanitAccess;

namespace Odmon.Worker.Services
{
    public class NetCourtDecisionAlertService
    {
        private const int StateId = 1;
        private readonly IntegrationDbContext _integrationDb;
        private readonly INetCourtDocumentReader _documentReader;
        private readonly INetCourtCaseResolver _caseResolver;
        private readonly IEmailNotifier _emailNotifier;
        private readonly IConfiguration _configuration;
        private readonly NetCourtDecisionAlertSettings _settings;
        private readonly ILogger<NetCourtDecisionAlertService> _logger;

        public NetCourtDecisionAlertService(
            IntegrationDbContext integrationDb,
            INetCourtDocumentReader documentReader,
            INetCourtCaseResolver caseResolver,
            IEmailNotifier emailNotifier,
            IConfiguration configuration,
            IOptions<NetCourtDecisionAlertSettings> settings,
            ILogger<NetCourtDecisionAlertService> logger)
        {
            _integrationDb = integrationDb;
            _documentReader = documentReader;
            _caseResolver = caseResolver;
            _emailNotifier = emailNotifier;
            _configuration = configuration;
            _settings = settings.Value;
            _logger = logger;
        }

        public async Task<NetCourtDecisionAlertRunResult> RunAsync(CancellationToken ct)
        {
            var result = new NetCourtDecisionAlertRunResult();
            var state = await _integrationDb.NetCourtDecisionAlertStates
                .SingleOrDefaultAsync(x => x.Id == StateId, ct);
            var firstRun = state?.BaselineCompletedAtUtc == null;

            if (firstRun && _settings.BaselineOnlyOnFirstRun)
            {
                var historicalRows = await _documentReader.GetDecisionDocumentsAsync(null, ct);
                result.CandidatesDetected = historicalRows.Count;
                result.Baselined = await RecordBaselineAsync(historicalRows, ct);

                state ??= new NetCourtDecisionAlertState { Id = StateId };
                state.BaselineCompletedAtUtc = DateTime.UtcNow;
                state.UpdatedAtUtc = DateTime.UtcNow;
                if (_integrationDb.Entry(state).State == EntityState.Detached)
                {
                    _integrationDb.NetCourtDecisionAlertStates.Add(state);
                }

                await _integrationDb.SaveChangesAsync(ct);
                _logger.LogInformation(
                    "NETCOURT baseline completed. Candidates={Candidates}, Recorded={Recorded}, EmailsSent=0",
                    historicalRows.Count,
                    result.Baselined);
                return result;
            }

            if (firstRun)
            {
                state ??= new NetCourtDecisionAlertState { Id = StateId };
                state.BaselineCompletedAtUtc = DateTime.UtcNow;
                state.UpdatedAtUtc = DateTime.UtcNow;
                _integrationDb.NetCourtDecisionAlertStates.Add(state);
                await _integrationDb.SaveChangesAsync(ct);
            }

            var lookbackDays = Math.Max(1, _settings.LookbackDays);
            var cutoffUtc = DateTime.UtcNow.AddDays(-lookbackDays);
            var candidates = await _documentReader.GetDecisionDocumentsAsync(cutoffUtc, ct);
            candidates = candidates
                .Where(IsDecisionDocument)
                .ToList();
            result.CandidatesDetected = candidates.Count;

            _logger.LogInformation(
                "NETCOURT candidates detected. Count={Count}, CutoffUtc={CutoffUtc:O}",
                candidates.Count,
                cutoffUtc);

            await AddNewTrackingRowsAsync(candidates, result, ct);
            await ProcessRetryableRowsAsync(result, ct);

            _logger.LogInformation(
                "NETCOURT run complete. Candidates={Candidates}, NewTracked={NewTracked}, AlreadyProcessed={AlreadyProcessed}, Queued={Queued}, MissingRouting={MissingRouting}, Failed={Failed}",
                result.CandidatesDetected,
                result.NewTracked,
                result.AlreadyProcessed,
                result.EmailsQueued,
                result.MissingRouting,
                result.Failed);

            return result;
        }

        private async Task<int> RecordBaselineAsync(
            IReadOnlyCollection<NetCourtDocument> documents,
            CancellationToken ct)
        {
            var identities = documents.Select(BuildDocumentIdentity).Distinct().ToArray();
            var existing = identities.Length == 0
                ? new HashSet<string>(StringComparer.Ordinal)
                : (await _integrationDb.NetCourtDecisionAlerts
                    .AsNoTracking()
                    .Where(x => identities.Contains(x.DocumentIdentity))
                    .Select(x => x.DocumentIdentity)
                    .ToListAsync(ct))
                    .ToHashSet(StringComparer.Ordinal);

            var added = 0;
            foreach (var document in documents.Where(IsDecisionDocument))
            {
                var identity = BuildDocumentIdentity(document);
                if (!existing.Add(identity))
                {
                    continue;
                }

                _integrationDb.NetCourtDecisionAlerts.Add(
                    CreateTrackingRow(document, identity, NetCourtDecisionAlertStatuses.Baseline));
                added++;
            }

            if (added > 0)
            {
                await _integrationDb.SaveChangesAsync(ct);
            }

            return added;
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

        private async Task ProcessRetryableRowsAsync(
            NetCourtDecisionAlertRunResult result,
            CancellationToken ct)
        {
            var rows = await _integrationDb.NetCourtDecisionAlerts
                .Where(x =>
                    x.Status == NetCourtDecisionAlertStatuses.Pending ||
                    x.Status == NetCourtDecisionAlertStatuses.ResolutionFailed ||
                    x.Status == NetCourtDecisionAlertStatuses.QueueFailed)
                .OrderBy(x => x.CreatedAtUtc)
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

                if (!_emailNotifier.QueueEmail(subject, body, actualRecipients))
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
                    _settings.IsTestMode
                        ? "NETCOURT test email queued. Identity={Identity}, TikNumber={TikNumber}, IntendedRecipient={IntendedRecipient}, ActualRecipient={ActualRecipient}"
                        : "NETCOURT live email queued. Identity={Identity}, TikNumber={TikNumber}, IntendedRecipient={IntendedRecipient}, ActualRecipient={ActualRecipient}",
                    row.DocumentIdentity,
                    row.TikNumber,
                    row.IntendedRecipientEmail,
                    row.ActualRecipientEmail);
            }

            await _integrationDb.SaveChangesAsync(ct);
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
        public int Baselined { get; set; }
        public int NewTracked { get; set; }
        public int AlreadyProcessed { get; set; }
        public int EmailsQueued { get; set; }
        public int MissingRouting { get; set; }
        public int Failed { get; set; }
    }
}
