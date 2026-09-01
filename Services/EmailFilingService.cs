using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Odmon.Worker.Configuration;
using Odmon.Worker.Data;
using Odmon.Worker.Models;
using Odmon.Worker.OdcanitAccess;

namespace Odmon.Worker.Services
{
    public sealed class EmailFilingProcessingException : Exception
    {
        public EmailFilingProcessingException(string message, Exception? innerException = null)
            : base(message, innerException)
        {
        }
    }

    /// <summary>
    /// TikNumber-authoritative email filing pipeline. Court-case resolution is
    /// observer-only and can never add a filing target.
    /// </summary>
    public sealed class EmailFilingService
    {
        private const int MaximumIdentifierLength = 64;
        private const int DefaultMaximumIdentifierCandidates = 100;
        private const int MaximumObserverClassificationsLength = 256;
        private const int MaximumObserverErrorCategoryLength = 128;
        private static readonly Regex TikNumberRegex = new(
            @"(?<![0-9/])[0-9]+/[0-9]+(?![0-9/])",
            RegexOptions.CultureInvariant | RegexOptions.Compiled,
            TimeSpan.FromSeconds(1));
        private static readonly Regex ScriptAndStyleRegex = new(
            @"<(script|style)\b[^>]*>.*?</\1\s*>",
            RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant | RegexOptions.Compiled,
            TimeSpan.FromSeconds(1));
        private static readonly Regex HtmlTagRegex = new(
            @"<[^>]+>",
            RegexOptions.Singleline | RegexOptions.CultureInvariant | RegexOptions.Compiled,
            TimeSpan.FromSeconds(1));

        private readonly IntegrationDbContext _db;
        private readonly IOdcanitReader _odcanitReader;
        private readonly IEmailAutomationCaseResolver _courtCaseResolver;
        private readonly IEmailCaseEvidenceExtractor _evidenceExtractor;
        private readonly IEmailCasePrimaryResolver _primaryResolver;
        private readonly IEmailCaseResolutionEngine _resolutionEngine;
        private readonly IEmailFilingGraphClient _graphClient;
        private readonly IEmailMsgGenerator _msgGenerator;
        private readonly IEmailFilingDocumentWriter _documentWriter;
        private readonly EmailFilingSettings _settings;
        private readonly EmailAutomationSettings _automationSettings;
        private readonly ILogger<EmailFilingService> _logger;
        private readonly IEmailNotifier _emailNotifier;
        private readonly TimeProvider _timeProvider;

        public EmailFilingService(
            IntegrationDbContext db,
            IOdcanitReader odcanitReader,
            IEmailAutomationCaseResolver courtCaseResolver,
            IEmailCaseEvidenceExtractor evidenceExtractor,
            IEmailCasePrimaryResolver primaryResolver,
            IEmailCaseResolutionEngine resolutionEngine,
            IEmailFilingGraphClient graphClient,
            IEmailMsgGenerator msgGenerator,
            IEmailFilingDocumentWriter documentWriter,
            IOptions<EmailFilingSettings> options,
            IOptions<EmailAutomationSettings> automationOptions,
            ILogger<EmailFilingService> logger,
            IEmailNotifier emailNotifier,
            TimeProvider timeProvider)
        {
            _db = db;
            _odcanitReader = odcanitReader;
            _courtCaseResolver = courtCaseResolver;
            _evidenceExtractor = evidenceExtractor;
            _primaryResolver = primaryResolver;
            _resolutionEngine = resolutionEngine;
            _graphClient = graphClient;
            _msgGenerator = msgGenerator;
            _documentWriter = documentWriter;
            _settings = options.Value;
            _automationSettings = automationOptions.Value;
            _logger = logger;
            _emailNotifier = emailNotifier;
            _timeProvider = timeProvider;
        }

        public async Task<EmailFilingDiagnostic?> ProcessAsync(
            string mailbox,
            IReadOnlyList<EmailAutomationRuleSettings> courtObserverRules,
            EmailAutomationMessage message,
            CancellationToken cancellationToken)
        {
            if (!_settings.Enabled)
            {
                return null;
            }

            if (string.IsNullOrWhiteSpace(_automationSettings.FingerprintKey))
            {
                throw new InvalidOperationException(
                    "EmailAutomation:FingerprintKey is required when EmailFiling is enabled.");
            }

            var normalizedBody = NormalizeBodyForDetection(message.Body, message.BodyContentType);
            EmailCaseEvidence? canonicalEvidence = null;
            var primarySnapshot = EmailCaseResolutionSnapshot.Empty;
            var resolutionAnalysis = EmailCaseResolutionAnalysis.Empty;
            string? observerErrorCategory = null;
            var extractionDurationMs = 0L;
            var primaryResolutionDurationMs = 0L;
            var supportingNarrowingDurationMs = 0L;
            var totalPhantomDurationMs = 0L;
            EmailCaseEvidence? authorityEvidence = null;
            var phantomStarted = Stopwatch.GetTimestamp();
            try
            {
                var stageStarted = Stopwatch.GetTimestamp();
                canonicalEvidence = _evidenceExtractor.Extract(
                    message.Subject,
                    normalizedBody,
                    _settings.MaxIdentifierCandidates,
                    message.Sender,
                    message.Body);
                extractionDurationMs = ElapsedMilliseconds(stageStarted);

                if (canonicalEvidence.SourceTemplate == EmailSourceTemplate.DirectInsurance &&
                    canonicalEvidence.PreferredClaimNumbers.Count > 0)
                {
                    authorityEvidence = CreateDirectInsuranceResolutionEvidence(canonicalEvidence);
                    stageStarted = Stopwatch.GetTimestamp();
                    primarySnapshot = await _primaryResolver.ResolveAsync(
                        authorityEvidence,
                        cancellationToken);
                    primaryResolutionDurationMs = ElapsedMilliseconds(stageStarted);

                    stageStarted = Stopwatch.GetTimestamp();
                    resolutionAnalysis = await _resolutionEngine.AnalyzeAsync(
                        authorityEvidence,
                        primarySnapshot,
                        cancellationToken);
                    supportingNarrowingDurationMs = ElapsedMilliseconds(stageStarted);
                }
                else
                {
                    authorityEvidence = canonicalEvidence;
                    stageStarted = Stopwatch.GetTimestamp();
                    primarySnapshot = await _primaryResolver.ResolveAsync(
                        authorityEvidence,
                        cancellationToken);
                    primaryResolutionDurationMs = ElapsedMilliseconds(stageStarted);

                    stageStarted = Stopwatch.GetTimestamp();
                    resolutionAnalysis = await _resolutionEngine.AnalyzeAsync(
                        authorityEvidence,
                        primarySnapshot,
                        cancellationToken);
                    supportingNarrowingDurationMs = ElapsedMilliseconds(stageStarted);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                observerErrorCategory = ex.GetType().Name[..Math.Min(
                    ex.GetType().Name.Length,
                    MaximumObserverErrorCategoryLength)];
                // Resolver failure remains isolated from established generic
                // TikNumber authority. Never log the exception message or evidence.
                _logger.LogWarning(
                    "EMAILFILING resolution failed. ErrorCategory={ErrorCategory}",
                    observerErrorCategory);
            }
            finally
            {
                totalPhantomDurationMs = ElapsedMilliseconds(phantomStarted);
            }

            var tikCandidates = ExtractTikNumberCandidates(
                message.Subject,
                normalizedBody,
                _settings.MaxIdentifierCandidates);
            var distinctTikNumbers = tikCandidates
                .Select(candidate => candidate.Value)
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            var lookupResults = await _odcanitReader.ResolveTikNumbersWithAmbiguityAsync(
                distinctTikNumbers,
                cancellationToken);
            var resolvedTikNumbers = lookupResults
                .Where(pair => pair.Value.IsResolved)
                .ToDictionary(
                    pair => pair.Key,
                    pair => pair.Value.TikCounter!.Value,
                    StringComparer.Ordinal);
            var ambiguousTikNumbers = lookupResults
                .Where(pair => pair.Value.IsAmbiguous)
                .Select(pair => pair.Key)
                .ToHashSet(StringComparer.Ordinal);

            var courtCandidates = ExtractCourtCaseCandidates(
                courtObserverRules,
                message.Subject,
                normalizedBody);
            var courtResolutions = await ResolveCourtCandidatesAsync(
                courtCandidates,
                cancellationToken);

            var messageFingerprint = CreateMessageFingerprint(mailbox, message);
            var genericResolvedTargets = resolvedTikNumbers
                .Where(pair => distinctTikNumbers.Contains(pair.Key, StringComparer.Ordinal))
                .GroupBy(pair => pair.Value)
                .Select(group => new ResolvedTarget(
                    group.Key,
                    group.Select(pair => pair.Key).OrderBy(x => x, StringComparer.Ordinal).First()))
                .OrderBy(target => target.TikCounter)
                .ToArray();
            var authorityDecision = authorityEvidence == null
                ? EmailFilingAuthorityDecision.Blocked(EmailFilingAuthorityKinds.BlockedResolverError)
                : authorityEvidence.SourceTemplate == EmailSourceTemplate.DirectInsurance
                    ? EmailFilingRealWriteGate.EvaluateDirect(
                        authorityEvidence,
                        resolutionAnalysis,
                        observerErrorCategory != null)
                    : EmailFilingRealWriteGate.EvaluateGeneric(
                        primarySnapshot,
                        resolutionAnalysis,
                        observerErrorCategory != null);
            ResolvedTarget[] resolvedTargets = [];
            if (authorityDecision is { IsAuthorized: true, TikCounter: not null })
            {
                var existingTarget = genericResolvedTargets.SingleOrDefault(target =>
                    target.TikCounter == authorityDecision.TikCounter.Value);
                resolvedTargets = existingTarget == null
                    ? await ResolveAuthorityTargetAsync(
                        authorityDecision.TikCounter.Value,
                        cancellationToken)
                    : [existingTarget];
            }
            var targetCounters = resolvedTargets.Select(target => target.TikCounter).ToArray();
            var writeStates = targetCounters.Length == 0
                ? []
                : await _db.EmailFilingDedups
                    .Where(row =>
                        row.MessageFingerprint == messageFingerprint &&
                        targetCounters.Contains(row.TikCounter))
                    .ToListAsync(cancellationToken);
            var writeStateByCounter = writeStates.ToDictionary(row => row.TikCounter);
            var duplicateCounters = writeStates
                .Where(row => row.Status == EmailFilingWriteStates.Succeeded)
                .Select(row => row.TikCounter)
                .ToHashSet();

            var resolvedCourtCounters = courtResolutions.Values
                .Where(match => match is { IsAmbiguous: false, TikCounter: > 0 })
                .Select(match => match!.TikCounter)
                .Distinct()
                .OrderBy(value => value)
                .ToArray();
            var observerClassifications = ClassifyObserverResults(
                targetCounters,
                resolvedCourtCounters)
                .Concat(resolutionAnalysis.ObserverClassifications)
                .Append($"REAL_WRITE_AUTHORITY_{authorityDecision.AuthorityKind}")
                .Distinct(StringComparer.Ordinal)
                .ToArray();

            var diagnostic = new EmailFilingDiagnostic
            {
                Mailbox = mailbox.Trim().ToLowerInvariant(),
                MessageFingerprint = messageFingerprint,
                ReceivedDateTimeUtc = message.ReceivedDateTimeUtc,
                TikCandidateCount = tikCandidates.Count,
                ResolvedTikCount = distinctTikNumbers.Count(resolvedTikNumbers.ContainsKey),
                SuspectNotCaseCount = distinctTikNumbers.Count(value => !resolvedTikNumbers.ContainsKey(value)),
                CourtCandidateCount = courtCandidates.Count,
                ResolvedCourtCount = resolvedCourtCounters.Length,
                TargetCount = resolvedTargets.Length,
                DedupHitCount = duplicateCounters.Count,
                ObserverClassifications = FormatObserverClassifications(observerClassifications),
                CreatedAtUtc = UtcNow()
            };

            foreach (var candidate in tikCandidates)
            {
                var isResolved = resolvedTikNumbers.TryGetValue(candidate.Value, out var tikCounter);
                var isAmbiguous = ambiguousTikNumbers.Contains(candidate.Value);
                diagnostic.Candidates.Add(new EmailFilingCandidateDiagnostic
                {
                    CandidateType = EmailFilingConstants.TikCandidateType,
                    Source = candidate.Source,
                    Candidate = candidate.Value,
                    ResolutionStatus = isResolved
                        ? EmailFilingConstants.Resolved
                        : isAmbiguous
                            ? EmailFilingConstants.TikAmbiguous
                            : EmailFilingConstants.SuspectNotCase,
                    ResolvedTikCounter = isResolved ? tikCounter : null,
                    ResolvedTikNumber = isResolved ? candidate.Value : null
                });
            }

            foreach (var candidate in courtCandidates)
            {
                courtResolutions.TryGetValue(candidate.Value, out var match);
                diagnostic.Candidates.Add(new EmailFilingCandidateDiagnostic
                {
                    CandidateType = EmailFilingConstants.CourtCandidateType,
                    Source = candidate.Source,
                    Candidate = candidate.Value,
                    ResolutionStatus = match == null
                        ? EmailFilingConstants.CourtNotFound
                        : match.IsAmbiguous
                            ? EmailFilingConstants.CourtAmbiguous
                            : EmailFilingConstants.Resolved,
                    ResolvedTikCounter = match is { IsAmbiguous: false } ? match.TikCounter : null,
                    ResolvedTikNumber = match is { IsAmbiguous: false } ? match.TikNumber : null
                });
            }

            foreach (var target in resolvedTargets)
            {
                var duplicate = duplicateCounters.Contains(target.TikCounter);
                var allowlisted = _settings.IsRealWriteAllowlisted(
                    target.TikNumber,
                    target.TikCounter);
                var authorized = _settings.IsRealWriteAuthorized(
                    target.TikNumber,
                    target.TikCounter);
                var decision = GetTargetDecision(duplicate, authorized);

                diagnostic.Targets.Add(new EmailFilingTargetDiagnostic
                {
                    TikCounter = target.TikCounter,
                    TikNumber = target.TikNumber,
                    RealWriteAllowlisted = allowlisted,
                    DedupResult = duplicate
                        ? EmailFilingConstants.Duplicate
                        : writeStateByCounter.ContainsKey(target.TikCounter)
                            ? EmailFilingConstants.RecoveryPending
                            : EmailFilingConstants.NotPreviouslyFiled,
                    Decision = decision
                });
            }

            if (_settings.ResolutionPhantomEnabled)
            {
                var preferredClaimUsed = canonicalEvidence?.SourceTemplate ==
                                         EmailSourceTemplate.DirectInsurance &&
                                         canonicalEvidence.PreferredClaimNumbers.Count > 0;
                diagnostic.ResolutionRun = EmailFilingResolutionTelemetry.CreateRun(
                    canonicalEvidence,
                    primarySnapshot,
                    resolutionAnalysis,
                    authorityDecision,
                    preferredClaimUsed,
                    targetCounters,
                    new EmailFilingResolutionTimings(
                        extractionDurationMs,
                        primaryResolutionDurationMs,
                        supportingNarrowingDurationMs,
                        totalPhantomDurationMs),
                    observerErrorCategory,
                    UtcNow());
            }

            _db.EmailFilingDiagnostics.Add(diagnostic);
            var readyTargets = diagnostic.Targets
                .Where(target => target.Decision == EmailFilingConstants.ReadyToFile)
                .ToArray();
            var writeFailed = false;
            if (readyTargets.Length > 0)
            {
                IEmailMsgArtifact artifact;
                try
                {
                    var mime = await _graphClient.GetMimeAsync(
                        diagnostic.Mailbox,
                        message.Id,
                        cancellationToken);
                    artifact = await _msgGenerator.GenerateAsync(mime, cancellationToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    foreach (var target in readyTargets)
                        target.Decision = EmailFilingConstants.MimeFailed;
                    diagnostic.FinalDecision = EmailFilingConstants.MimeFailed;
                    await _db.SaveChangesAsync(cancellationToken);
                    _logger.LogError(
                        "EMAILFILING MIME preparation failed. TargetCount={TargetCount}, ErrorCategory={ErrorCategory}",
                        readyTargets.Length,
                        ex.GetType().Name);
                    throw new EmailFilingProcessingException(
                        "Email MIME retrieval or MSG generation failed.",
                        ex);
                }

                await using (artifact)
                {
                    long expectedFileLength;
                    try
                    {
                        expectedFileLength = await _documentWriter.PreflightAsync(
                            artifact.FilePath,
                            cancellationToken);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        foreach (var target in readyTargets)
                            target.Decision = EmailFilingConstants.MimeFailed;
                        diagnostic.FinalDecision = EmailFilingConstants.MimeFailed;
                        await _db.SaveChangesAsync(cancellationToken);
                        _logger.LogError(
                            "EMAILFILING MSG preflight failed. TargetCount={TargetCount}, ErrorCategory={ErrorCategory}",
                            readyTargets.Length,
                            ex.GetType().Name);
                        throw new EmailFilingProcessingException(
                            "Generated MSG failed preflight validation before any Odcanit write.",
                            ex);
                    }

                    var emailDateUtc = message.SentDateTimeUtc ??
                                       message.ReceivedDateTimeUtc ??
                                       UtcNow();
                    foreach (var target in readyTargets)
                    {
                        try
                        {
                            writeStateByCounter.TryGetValue(target.TikCounter, out var writeState);
                            var filed = await WriteOrResumeTargetAsync(
                                writeState,
                                messageFingerprint,
                                target,
                                message.Subject,
                                artifact.FilePath,
                                emailDateUtc,
                                expectedFileLength,
                                cancellationToken);
                            target.Decision = filed
                                ? EmailFilingConstants.Filed
                                : EmailFilingConstants.ManualRepairRequired;
                            writeFailed |= !filed;
                        }
                        catch (Exception ex) when (ex is not OperationCanceledException)
                        {
                            writeFailed = true;
                            if (target.Decision == EmailFilingConstants.ReadyToFile)
                                target.Decision = EmailFilingConstants.WriteFailed;
                            _logger.LogError(
                                "EMAILFILING document write failed. TikCounter={TikCounter}, ErrorCategory={ErrorCategory}",
                                target.TikCounter,
                                ex.GetType().Name);
                        }
                    }
                }
            }

            diagnostic.FinalDecision = diagnostic.Targets.Any(
                    target => target.Decision == EmailFilingConstants.ManualRepairRequired)
                ? EmailFilingConstants.ManualRepairRequired
                : writeFailed
                    ? EmailFilingConstants.WriteFailed
                : readyTargets.Length > 0
                    ? EmailFilingConstants.Filed
                    : GetFinalDecision(
                        tikCandidates.Count +
                        (canonicalEvidence?.PreferredClaimNumbers.Count ?? 0) +
                        (canonicalEvidence?.CourtCaseNumbers.Count ?? 0),
                        resolvedTargets.Length,
                        diagnostic.Targets);
            await _db.SaveChangesAsync(cancellationToken);

            _logger.LogInformation(
                "EMAILFILING processing completed. TikCandidates=[{TikCandidates}], ResolvedTargets=[{ResolvedTargets}], AmbiguousTik=[{AmbiguousTik}], SuspectNotCase=[{SuspectNotCase}], CourtClassifications={CourtClassifications}, DedupHits={DedupHits}, FinalDecision={FinalDecision}",
                string.Join(",", tikCandidates.Select(candidate => $"{candidate.Value}:{candidate.Source}")),
                string.Join(",", resolvedTargets.Select(target => $"{target.TikNumber}:{target.TikCounter}")),
                string.Join(",", ambiguousTikNumbers),
                string.Join(",", distinctTikNumbers.Where(value =>
                    !resolvedTikNumbers.ContainsKey(value) && !ambiguousTikNumbers.Contains(value))),
                diagnostic.ObserverClassifications,
                diagnostic.DedupHitCount,
                diagnostic.FinalDecision);

            if (writeFailed)
            {
                throw new EmailFilingProcessingException(
                    "One or more EmailFiling document targets failed; the delta cursor must not advance.");
            }

            return diagnostic;
        }

        private static long ElapsedMilliseconds(long startedTimestamp)
            => Math.Max(0L, (long)Math.Ceiling(
                Stopwatch.GetElapsedTime(startedTimestamp).TotalMilliseconds));

        internal static IReadOnlyList<IdentifierCandidate> ExtractTikNumberCandidates(
            string? subject,
            string? normalizedBody,
            int maximumCandidates = DefaultMaximumIdentifierCandidates)
        {
            var candidates = new List<IdentifierCandidate>();
            var matchCount = 0;
            AddMatches(
                TikNumberRegex,
                subject,
                EmailFilingConstants.SubjectSource,
                candidates,
                maximumCandidates,
                ref matchCount);
            AddMatches(
                TikNumberRegex,
                normalizedBody,
                EmailFilingConstants.BodySource,
                candidates,
                maximumCandidates,
                ref matchCount);
            return candidates
                .DistinctBy(candidate => (candidate.Source, candidate.Value))
                .ToArray();
        }

        internal static string NormalizeBodyForDetection(string? body, string? contentType)
        {
            if (string.IsNullOrWhiteSpace(body))
            {
                return string.Empty;
            }

            if (!string.Equals(contentType, "html", StringComparison.OrdinalIgnoreCase))
            {
                return body;
            }

            var withoutExecutableContent = ScriptAndStyleRegex.Replace(body, " ");
            var withoutTags = HtmlTagRegex.Replace(withoutExecutableContent, string.Empty);
            return WebUtility.HtmlDecode(withoutTags);
        }

        internal static IReadOnlyList<string> ClassifyObserverResults(
            IEnumerable<int> tikCounters,
            IEnumerable<int> courtCounters)
        {
            var tik = tikCounters.Distinct().ToHashSet();
            var court = courtCounters.Distinct().ToHashSet();
            var classifications = new List<string>();

            if (tik.Count > 0 && court.Count == 0)
            {
                classifications.Add("TIK_ONLY");
            }
            else if (tik.Count == 0 && court.Count > 0)
            {
                classifications.Add("COURT_ONLY");
            }
            else if (tik.Count > 0 && court.Count > 0)
            {
                if (tik.SetEquals(court))
                {
                    classifications.Add("AGREEMENT");
                }
                else if (tik.Overlaps(court))
                {
                    classifications.Add("PARTIAL_OVERLAP");
                }
                else
                {
                    classifications.Add("CONFLICT");
                }
            }

            if (tik.Count > 1)
            {
                classifications.Add("MULTI_TIK");
            }

            if (court.Count > 1)
            {
                classifications.Add("MULTI_COURT");
            }

            return classifications.Count == 0 ? ["NONE"] : classifications;
        }

        internal static string FormatObserverClassifications(IEnumerable<string> values)
        {
            const string truncated = "OBSERVER_TRUNCATED";
            var classifications = values
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            var complete = string.Join(",", classifications);
            if (complete.Length <= MaximumObserverClassificationsLength)
                return complete;

            var retained = new List<string>();
            foreach (var classification in classifications)
            {
                var candidate = string.Join(",", retained.Append(classification).Append(truncated));
                if (candidate.Length > MaximumObserverClassificationsLength)
                    break;
                retained.Add(classification);
            }
            retained.Add(truncated);
            return string.Join(",", retained);
        }

        private IReadOnlyList<IdentifierCandidate> ExtractCourtCaseCandidates(
            IReadOnlyList<EmailAutomationRuleSettings> rules,
            string? subject,
            string? normalizedBody)
        {
            var candidates = new List<IdentifierCandidate>();
            var matchCount = 0;
            foreach (var pattern in rules
                         .Where(rule => rule.Enabled && !string.IsNullOrWhiteSpace(rule.SubjectRegex))
                         .Select(rule => rule.SubjectRegex)
                         .Distinct(StringComparer.Ordinal))
            {
                try
                {
                    var regex = new Regex(
                        pattern,
                        RegexOptions.CultureInvariant,
                        TimeSpan.FromSeconds(1));
                    AddMatches(
                        regex,
                        subject,
                        EmailFilingConstants.SubjectSource,
                        candidates,
                        _settings.MaxIdentifierCandidates,
                        ref matchCount);
                    AddMatches(
                        regex,
                        normalizedBody,
                        EmailFilingConstants.BodySource,
                        candidates,
                        _settings.MaxIdentifierCandidates,
                        ref matchCount);
                }
                catch (ArgumentException)
                {
                    _logger.LogWarning(
                        "EMAILFILING court observer ignored an invalid EmailAutomation subject regex.");
                }
                catch (RegexMatchTimeoutException)
                {
                    _logger.LogWarning(
                        "EMAILFILING court observer regex timed out and was ignored for one email.");
                }
            }

            return candidates
                .DistinctBy(candidate => (candidate.Source, candidate.Value))
                .ToArray();
        }

        private async Task<Dictionary<string, EmailAutomationCaseMatch?>> ResolveCourtCandidatesAsync(
            IReadOnlyList<IdentifierCandidate> candidates,
            CancellationToken cancellationToken)
        {
            var results = new Dictionary<string, EmailAutomationCaseMatch?>(StringComparer.Ordinal);
            foreach (var courtCaseNumber in candidates
                         .Select(candidate => candidate.Value)
                         .Distinct(StringComparer.Ordinal))
            {
                results[courtCaseNumber] = await _courtCaseResolver.ResolveByCourtCaseNumberAsync(
                    courtCaseNumber,
                    cancellationToken);
            }

            return results;
        }

        private async Task<bool> WriteOrResumeTargetAsync(
            EmailFilingDedup? state,
            string messageFingerprint,
            EmailFilingTargetDiagnostic target,
            string? subject,
            string msgFilePath,
            DateTime emailDateUtc,
            long expectedFileLength,
            CancellationToken cancellationToken)
        {
            string? destinationPath = null;
            if (state == null)
            {
                state = new EmailFilingDedup
                {
                    MessageFingerprint = messageFingerprint,
                    TikCounter = target.TikCounter,
                    TikNumber = target.TikNumber,
                    Status = EmailFilingWriteStates.Reserved,
                    ExpectedFileLength = expectedFileLength,
                    CreatedAtUtc = UtcNow(),
                    UpdatedAtUtc = UtcNow()
                };
                _db.EmailFilingDedups.Add(state);
                await _db.SaveChangesAsync(cancellationToken);
            }

            if (state.Status == EmailFilingWriteStates.Succeeded)
                return true;

            if (state.Status is EmailFilingWriteStates.CreatingDocument or
                EmailFilingWriteStates.CreateUncertain)
            {
                target.Decision = EmailFilingConstants.ManualRepairRequired;
                RaiseCriticalOperationalDiagnostic(
                    "Odcanit document creation outcome is uncertain",
                    state,
                    state.LastErrorCategory ?? "ProcessInterruption");
                return false;
            }

            if (state.Status == EmailFilingWriteStates.Reserved)
            {
                state.ExpectedFileLength = expectedFileLength;
                state.Status = EmailFilingWriteStates.CreatingDocument;
                state.LastErrorCategory = null;
                state.UpdatedAtUtc = UtcNow();
                await _db.SaveChangesAsync(cancellationToken);

                EmailFilingDocumentDestination created;
                try
                {
                    created = await _documentWriter.CreateDocumentRowAsync(
                        target.TikCounter,
                        subject ?? string.Empty,
                        msgFilePath,
                        emailDateUtc,
                        cancellationToken);
                }
                catch (Exception ex)
                {
                    state.Status = EmailFilingWriteStates.CreateUncertain;
                    state.LastErrorCategory = ex.GetType().Name;
                    state.UpdatedAtUtc = UtcNow();
                    await TryPersistRecoveryStateAsync(state);
                    target.Decision = EmailFilingConstants.ManualRepairRequired;
                    RaiseCriticalOperationalDiagnostic(
                        "Odcanit document creation outcome is uncertain",
                        state,
                        ex.GetType().Name);
                    if (ex is OperationCanceledException)
                        throw;
                    return false;
                }

                state.OdcanitDocCounter = created.DocCounter;
                destinationPath = created.DestPath;
                state.Status = EmailFilingWriteStates.DocumentCreated;
                state.UpdatedAtUtc = UtcNow();
                try
                {
                    await _db.SaveChangesAsync(cancellationToken);
                }
                catch (Exception ex)
                {
                    // The SP has returned a confirmed DocCounter, but IntegrationDb
                    // may still contain CREATING_DOCUMENT. Never call the SP again.
                    target.Decision = EmailFilingConstants.ManualRepairRequired;
                    RaiseCriticalOperationalDiagnostic(
                        "Odcanit document was created but its DocCounter could not be persisted",
                        state,
                        ex.GetType().Name);
                    throw;
                }
            }

            if (state.Status is not (EmailFilingWriteStates.DocumentCreated or
                EmailFilingWriteStates.Copying or
                EmailFilingWriteStates.CopyFailed))
            {
                target.Decision = EmailFilingConstants.ManualRepairRequired;
                RaiseCriticalOperationalDiagnostic(
                    "Email filing write state is not safely resumable",
                    state,
                    "InvalidWriteState");
                return false;
            }

            if (!state.OdcanitDocCounter.HasValue || state.OdcanitDocCounter <= 0)
            {
                state.Status = EmailFilingWriteStates.CreateUncertain;
                state.LastErrorCategory = "MissingDocumentCoordinates";
                state.UpdatedAtUtc = UtcNow();
                await TryPersistRecoveryStateAsync(state);
                target.Decision = EmailFilingConstants.ManualRepairRequired;
                RaiseCriticalOperationalDiagnostic(
                    "Email filing DocCounter is missing",
                    state,
                    state.LastErrorCategory);
                return false;
            }

            if (string.IsNullOrWhiteSpace(destinationPath))
            {
                try
                {
                    destinationPath = await _documentWriter.ResolveDestinationPathAsync(
                        state.OdcanitDocCounter.Value,
                        cancellationToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    await RecordCopyFailureAsync(state, ex);
                    throw;
                }
            }

            var overwriteExisting = state.Status is
                EmailFilingWriteStates.Copying or EmailFilingWriteStates.CopyFailed;
            if (overwriteExisting)
            {
                bool destinationAlreadyVerified;
                try
                {
                    destinationAlreadyVerified = await _documentWriter.IsVerifiedDestinationAsync(
                        destinationPath,
                        state.ExpectedFileLength,
                        cancellationToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    await RecordCopyFailureAsync(state, ex);
                    throw;
                }

                if (destinationAlreadyVerified)
                {
                    await MarkSucceededAsync(state, cancellationToken);
                    _logger.LogInformation(
                        "EMAILFILING recovered a previously verified copy. ReservationId={ReservationId}, TikCounter={TikCounter}, DocCounter={DocCounter}",
                        state.Id,
                        state.TikCounter,
                        state.OdcanitDocCounter);
                    return true;
                }
            }

            state.ExpectedFileLength = expectedFileLength;
            state.Status = EmailFilingWriteStates.Copying;
            state.LastErrorCategory = null;
            state.UpdatedAtUtc = UtcNow();
            await _db.SaveChangesAsync(cancellationToken);

            try
            {
                await _documentWriter.CopyAndVerifyAsync(
                    msgFilePath,
                    destinationPath,
                    expectedFileLength,
                    overwriteExisting,
                    cancellationToken);
            }
            catch (Exception ex)
            {
                await RecordCopyFailureAsync(state, ex);
                throw;
            }

            // COPY success is recoverable even if this save fails: COPYING was
            // persisted first, and a retry resolves and verifies the same DocCounter rather than
            // creating a second Odcanit Documents row.
            await MarkSucceededAsync(state, cancellationToken);
            return true;
        }

        private async Task MarkSucceededAsync(
            EmailFilingDedup state,
            CancellationToken cancellationToken)
        {
            state.Status = EmailFilingWriteStates.Succeeded;
            state.FiledAtUtc = UtcNow();
            state.LastErrorCategory = null;
            state.UpdatedAtUtc = state.FiledAtUtc.Value;
            await _db.SaveChangesAsync(cancellationToken);
        }

        private async Task RecordCopyFailureAsync(EmailFilingDedup state, Exception ex)
        {
            state.Status = EmailFilingWriteStates.CopyFailed;
            state.LastErrorCategory = ex.GetType().Name;
            state.UpdatedAtUtc = UtcNow();
            await TryPersistRecoveryStateAsync(state);
            RaiseCriticalOperationalDiagnostic(
                "Odcanit document row exists but MSG copy or verification failed",
                state,
                ex.GetType().Name);
        }

        private async Task TryPersistRecoveryStateAsync(EmailFilingDedup state)
        {
            try
            {
                await _db.SaveChangesAsync(CancellationToken.None);
            }
            catch (Exception persistenceException)
            {
                _logger.LogCritical(
                    "EMAILFILING recovery state persistence failed. ReservationId={ReservationId}, TikCounter={TikCounter}, DocCounter={DocCounter}, State={State}, ErrorCategory={ErrorCategory}",
                    state.Id,
                    state.TikCounter,
                    state.OdcanitDocCounter,
                    state.Status,
                    persistenceException.GetType().Name);
            }
        }

        private void RaiseCriticalOperationalDiagnostic(
            string title,
            EmailFilingDedup state,
            string errorCategory)
        {
            _logger.LogCritical(
                "EMAILFILING CRITICAL | {Title} | ReservationId={ReservationId}, TikCounter={TikCounter}, DocCounter={DocCounter}, State={State}, ErrorCategory={ErrorCategory}",
                title,
                state.Id,
                state.TikCounter,
                state.OdcanitDocCounter,
                state.Status,
                errorCategory);

            var body = string.Join(
                Environment.NewLine,
                $"ReservationId: {state.Id}",
                $"TikCounter: {state.TikCounter}",
                $"DocCounter: {state.OdcanitDocCounter?.ToString() ?? "UNKNOWN"}",
                $"State: {state.Status}",
                $"ErrorCategory: {errorCategory}");
            _emailNotifier.QueueCriticalAlert(
                title,
                body,
                errorCategory,
                "EmailFilingService",
                "Email Filing Manual Repair");
        }

        private string GetTargetDecision(bool duplicate, bool authorized)
        {
            if (duplicate)
            {
                return EmailFilingConstants.SkipDuplicate;
            }

            if (_settings.DryRun)
            {
                return EmailFilingConstants.DryRunWouldFile;
            }

            if (!_settings.RealWriteEnabled)
            {
                return EmailFilingConstants.RealWriteDisabled;
            }

            if (!authorized)
            {
                return EmailFilingConstants.NotAllowlisted;
            }

            return EmailFilingConstants.ReadyToFile;
        }

        private static EmailCaseEvidence CreateDirectInsuranceResolutionEvidence(
            EmailCaseEvidence evidence)
            => new(
                [],
                evidence.PreferredClaimNumbers,
                evidence.CourtCaseNumbers,
                evidence.VehicleNumbers,
                evidence.EventDates,
                evidence.ClientHints,
                evidence.InsuredNames,
                evidence.DriverPhones)
            {
                SourceTemplate = evidence.SourceTemplate,
                PreferredClaimNumbers = evidence.PreferredClaimNumbers
            };

        private async Task<ResolvedTarget[]> ResolveAuthorityTargetAsync(
            int tikCounter,
            CancellationToken cancellationToken)
        {
            var tikNumbers = await _odcanitReader.ResolveTikCountersToNumbersAsync(
                [tikCounter],
                cancellationToken);
            return tikNumbers.TryGetValue(tikCounter, out var tikNumber)
                ? [new ResolvedTarget(tikCounter, tikNumber)]
                : [];
        }

        private string GetFinalDecision(
            int tikCandidateCount,
            int targetCount,
            IReadOnlyCollection<EmailFilingTargetDiagnostic> targets)
        {
            if (tikCandidateCount == 0)
            {
                return EmailFilingConstants.NoTikCandidates;
            }

            if (targetCount == 0)
            {
                return EmailFilingConstants.NoValidTik;
            }

            if (targets.All(target => target.Decision == EmailFilingConstants.SkipDuplicate))
            {
                return EmailFilingConstants.AllTargetsDuplicate;
            }

            if (targets.Any(target => target.Decision == EmailFilingConstants.DryRunWouldFile))
            {
                return EmailFilingConstants.DryRunWouldFile;
            }

            if (targets.Any(target => target.Decision == EmailFilingConstants.RealWriteDisabled))
            {
                return EmailFilingConstants.RealWriteDisabled;
            }

            return EmailFilingConstants.NotAllowlisted;
        }

        private string CreateMessageFingerprint(string mailbox, EmailAutomationMessage message)
        {
            var stableIdentifier = !string.IsNullOrWhiteSpace(message.InternetMessageId)
                ? $"internet:{message.InternetMessageId.Trim()}"
                : $"graph:{message.Id.Trim()}";
            // Internet Message-ID identifies the same email across monitored
            // mailboxes. Graph IDs are mailbox-scoped, so only that fallback
            // incorporates the mailbox into the identity.
            var value = stableIdentifier.StartsWith("internet:", StringComparison.Ordinal)
                ? string.Join("\n", "email-filing-v1", stableIdentifier)
                : string.Join(
                    "\n",
                    "email-filing-v1",
                    mailbox.Trim().ToLowerInvariant(),
                    stableIdentifier);
            using var hmac = new HMACSHA256(
                Encoding.UTF8.GetBytes(_automationSettings.FingerprintKey));
            return Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(value)));
        }

        private static void AddMatches(
            Regex regex,
            string? input,
            string source,
            ICollection<IdentifierCandidate> destination,
            int maximumCandidates,
            ref int matchCount)
        {
            if (maximumCandidates <= 0)
                throw new InvalidOperationException("EmailFiling identifier-count limit must be positive.");

            if (string.IsNullOrWhiteSpace(input))
            {
                return;
            }

            foreach (Match match in regex.Matches(input))
            {
                if (!match.Success)
                    continue;

                matchCount++;
                if (matchCount > maximumCandidates)
                {
                    throw new InvalidDataException(
                        "Email contains more identifier candidates than the configured safety limit.");
                }

                if (match.Length <= MaximumIdentifierLength &&
                    !string.IsNullOrWhiteSpace(match.Value))
                {
                    destination.Add(new IdentifierCandidate(match.Value, source));
                }
            }
        }

        private DateTime UtcNow() => _timeProvider.GetUtcNow().UtcDateTime;

        internal sealed record IdentifierCandidate(string Value, string Source);
        private sealed record ResolvedTarget(int TikCounter, string TikNumber);
    }
}
