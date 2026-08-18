using Microsoft.Extensions.Logging;
using Odmon.Worker.Models;
using Odmon.Worker.OdcanitAccess;

namespace Odmon.Worker.Services
{
    public sealed class CaseIntakeReadService
    {
        private readonly ICaseIntakeDocumentReader _documentReader;
        private readonly IPdfTextExtractor _pdfTextExtractor;
        private readonly DigitalNotificationFormParser _notificationFormParser;
        private readonly DemandFormParser _demandFormParser;
        private readonly CaseIntakeResultMerger _resultMerger;
        private readonly ILogger<CaseIntakeReadService> _logger;

        public CaseIntakeReadService(
            ICaseIntakeDocumentReader documentReader,
            IPdfTextExtractor pdfTextExtractor,
            DigitalNotificationFormParser notificationFormParser,
            DemandFormParser demandFormParser,
            CaseIntakeResultMerger resultMerger,
            ILogger<CaseIntakeReadService> logger)
        {
            _documentReader = documentReader;
            _pdfTextExtractor = pdfTextExtractor;
            _notificationFormParser = notificationFormParser;
            _demandFormParser = demandFormParser;
            _resultMerger = resultMerger;
            _logger = logger;
        }

        public async Task<CaseIntakeReadResult> ReadAsync(int tikCounter, CancellationToken ct)
        {
            if (tikCounter <= 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(tikCounter),
                    tikCounter,
                    "TikCounter must be a positive integer.");
            }

            var sourceDocuments = await _documentReader.GetRelevantDocumentsAsync(tikCounter, ct);
            var documents = sourceDocuments
                .Select(Classify)
                .Where(document => document != null)
                .Cast<ClassifiedCaseDocument>()
                .ToArray();

            var notificationResults = new List<NotificationFormReadResult>();
            foreach (var document in documents.Where(document =>
                         document.BusinessType == CaseIntakeDocumentType.NotificationForm))
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    var text = await _pdfTextExtractor.ExtractTextAsync(
                        document.Source.Path ?? string.Empty,
                        ct);
                    var fields = _notificationFormParser.Parse(text, document.Source);
                    notificationResults.Add(new(
                        document,
                        CaseIntakeDocumentReadStatus.Parsed,
                        fields,
                        null));

                    _logger.LogInformation(
                        "CASE_INTAKE_READ | Parsed notification document. TikCounter={TikCounter}, DocumentId={DocumentId}, Valid={ValidCount}, Missing={MissingCount}, Invalid={InvalidCount}, Ambiguous={AmbiguousCount}",
                        tikCounter,
                        document.Source.Id,
                        CountStatus(fields, CaseIntakeFieldStatus.Valid),
                        CountStatus(fields, CaseIntakeFieldStatus.Missing),
                        CountStatus(fields, CaseIntakeFieldStatus.Invalid),
                        CountStatus(fields, CaseIntakeFieldStatus.Ambiguous));
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(
                        ex,
                        "CASE_INTAKE_READ | Notification document extraction failed. TikCounter={TikCounter}, DocumentId={DocumentId}, ErrorType={ErrorType}",
                        tikCounter,
                        document.Source.Id,
                        ex.GetType().Name);
                    notificationResults.Add(new(
                        document,
                        CaseIntakeDocumentReadStatus.ExtractionFailed,
                        null,
                        ex.Message));
                }
            }

            var demandResults = new List<DemandFormReadResult>();
            foreach (var document in documents.Where(document =>
                         document.BusinessType == CaseIntakeDocumentType.DemandForm))
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    var text = await _pdfTextExtractor.ExtractTextAsync(
                        document.Source.Path ?? string.Empty,
                        ct);
                    var fields = _demandFormParser.Parse(text, document.Source);
                    demandResults.Add(new(
                        document,
                        CaseIntakeDocumentReadStatus.Parsed,
                        fields,
                        null));

                    _logger.LogInformation(
                        "CASE_INTAKE_READ | Parsed demand document. TikCounter={TikCounter}, DocumentId={DocumentId}, Valid={ValidCount}, Missing={MissingCount}, Invalid={InvalidCount}, Ambiguous={AmbiguousCount}, FinancialCandidates={FinancialCandidateCount}",
                        tikCounter,
                        document.Source.Id,
                        CountStatus(fields, CaseIntakeFieldStatus.Valid),
                        CountStatus(fields, CaseIntakeFieldStatus.Missing),
                        CountStatus(fields, CaseIntakeFieldStatus.Invalid),
                        CountStatus(fields, CaseIntakeFieldStatus.Ambiguous),
                        fields.FinancialCandidates.Count);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(
                        ex,
                        "CASE_INTAKE_READ | Demand document extraction failed. TikCounter={TikCounter}, DocumentId={DocumentId}, ErrorType={ErrorType}",
                        tikCounter,
                        document.Source.Id,
                        ex.GetType().Name);
                    demandResults.Add(new(
                        document,
                        CaseIntakeDocumentReadStatus.ExtractionFailed,
                        null,
                        ex.Message));
                }
            }

            var mergedFields = _resultMerger.Merge(notificationResults, demandResults);

            _logger.LogInformation(
                "CASE_INTAKE_READ | Completed read-only case intake. TikCounter={TikCounter}, RelevantDocuments={RelevantDocumentCount}, NotificationForms={NotificationFormCount}, DemandForms={DemandFormCount}, MergedConflicts={MergedConflictCount}",
                tikCounter,
                documents.Length,
                notificationResults.Count,
                demandResults.Count,
                GetMergedConflicts(mergedFields).Count(hasConflict => hasConflict));

            return new CaseIntakeReadResult(
                tikCounter,
                documents,
                notificationResults,
                demandResults,
                mergedFields);
        }

        private static ClassifiedCaseDocument? Classify(OdcanitCaseDocument source)
        {
            return CaseIntakeDocumentClassifier.TryClassify(
                source.Name,
                out var type,
                out var strength)
                ? new ClassifiedCaseDocument(source, type, strength)
                : null;
        }

        private static int CountStatus(
            NotificationFormFields fields,
            CaseIntakeFieldStatus status)
            => GetStatuses(fields).Count(fieldStatus => fieldStatus == status);

        private static IEnumerable<CaseIntakeFieldStatus> GetStatuses(NotificationFormFields fields)
        {
            yield return fields.ClaimNumber.Status;
            yield return fields.EventDate.Status;
            yield return fields.PolicyNumber.Status;
            yield return fields.PolicyHolderName.Status;
            yield return fields.PolicyHolderId.Status;
            yield return fields.PolicyHolderPhone.Status;
            yield return fields.DriverName.Status;
            yield return fields.DriverId.Status;
            yield return fields.DriverPhone.Status;
            yield return fields.MainCarNumber.Status;
            yield return fields.ThirdPartyCarNumber.Status;
        }

        private static int CountStatus(
            DemandFormFields fields,
            CaseIntakeFieldStatus status)
            => GetStatuses(fields).Count(fieldStatus => fieldStatus == status);

        private static IEnumerable<CaseIntakeFieldStatus> GetStatuses(DemandFormFields fields)
        {
            yield return fields.ClaimNumber.Status;
            yield return fields.EventDate.Status;
            yield return fields.PolicyNumber.Status;
            yield return fields.PolicyHolderName.Status;
            yield return fields.PolicyHolderId.Status;
            yield return fields.MainCarNumber.Status;
            yield return fields.ThirdPartyCarNumber.Status;
            yield return fields.AppraiserFeeAmount.Status;
            yield return fields.LossOfValueAmount.Status;
        }

        private static IEnumerable<bool> GetMergedConflicts(MergedCaseIntakeFields fields)
        {
            yield return fields.ClaimNumber.HasConflict;
            yield return fields.EventDate.HasConflict;
            yield return fields.PolicyNumber.HasConflict;
            yield return fields.PolicyHolderName.HasConflict;
            yield return fields.PolicyHolderId.HasConflict;
            yield return fields.PolicyHolderPhone.HasConflict;
            yield return fields.DriverName.HasConflict;
            yield return fields.DriverId.HasConflict;
            yield return fields.DriverPhone.HasConflict;
            yield return fields.MainCarNumber.HasConflict;
            yield return fields.ThirdPartyCarNumber.HasConflict;
            yield return fields.AppraiserFeeAmount.HasConflict;
            yield return fields.LossOfValueAmount.HasConflict;
        }
    }
}
