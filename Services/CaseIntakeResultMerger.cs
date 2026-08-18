using Odmon.Worker.Models;

namespace Odmon.Worker.Services
{
    public sealed class CaseIntakeResultMerger
    {
        public MergedCaseIntakeFields Merge(
            IReadOnlyList<NotificationFormReadResult> notificationForms,
            IReadOnlyList<DemandFormReadResult> demandForms)
        {
            var notifications = notificationForms
                .Where(result => result.Status == CaseIntakeDocumentReadStatus.Parsed && result.Fields != null)
                .ToArray();
            var demands = demandForms
                .Where(result => result.Status == CaseIntakeDocumentReadStatus.Parsed && result.Fields != null)
                .ToArray();

            return new MergedCaseIntakeFields(
                ClaimNumber: MergeField(
                    notifications.Select(result => Source(result.Fields!.ClaimNumber, result.Document))
                        .Concat(demands.Select(result => Source(result.Fields!.ClaimNumber, result.Document))),
                    StringComparer.OrdinalIgnoreCase),
                EventDate: MergeField(
                    notifications.Select(result => Source(result.Fields!.EventDate, result.Document))
                        .Concat(demands.Select(result => Source(result.Fields!.EventDate, result.Document)))),
                PolicyNumber: MergeField(
                    notifications.Select(result => Source(result.Fields!.PolicyNumber, result.Document))
                        .Concat(demands.Select(result => Source(result.Fields!.PolicyNumber, result.Document))),
                    StringComparer.OrdinalIgnoreCase),
                PolicyHolderName: MergeField(
                    notifications.Select(result => Source(result.Fields!.PolicyHolderName, result.Document))
                        .Concat(demands.Select(result => Source(result.Fields!.PolicyHolderName, result.Document))),
                    StringComparer.OrdinalIgnoreCase),
                PolicyHolderId: MergeField(
                    notifications.Select(result => Source(result.Fields!.PolicyHolderId, result.Document))
                        .Concat(demands.Select(result => Source(result.Fields!.PolicyHolderId, result.Document))),
                    StringComparer.Ordinal),
                PolicyHolderPhone: MergeField(
                    notifications.Select(result => Source(result.Fields!.PolicyHolderPhone, result.Document)),
                    StringComparer.Ordinal),
                DriverName: MergeField(
                    notifications.Select(result => Source(result.Fields!.DriverName, result.Document)),
                    StringComparer.OrdinalIgnoreCase),
                DriverId: MergeField(
                    notifications.Select(result => Source(result.Fields!.DriverId, result.Document)),
                    StringComparer.Ordinal),
                DriverPhone: MergeField(
                    notifications.Select(result => Source(result.Fields!.DriverPhone, result.Document)),
                    StringComparer.Ordinal),
                MainCarNumber: MergeField(
                    notifications.Select(result => Source(result.Fields!.MainCarNumber, result.Document))
                        .Concat(demands.Select(result => Source(result.Fields!.MainCarNumber, result.Document))),
                    StringComparer.Ordinal),
                ThirdPartyCarNumber: MergeField(
                    notifications.Select(result => Source(result.Fields!.ThirdPartyCarNumber, result.Document))
                        .Concat(demands.Select(result => Source(result.Fields!.ThirdPartyCarNumber, result.Document))),
                    StringComparer.Ordinal),
                AppraiserFeeAmount: MergeField(
                    demands.Select(result => Source(result.Fields!.AppraiserFeeAmount, result.Document))),
                LossOfValueAmount: MergeField(
                    demands.Select(result => Source(result.Fields!.LossOfValueAmount, result.Document))));
        }

        private static FieldWithSource<TValue> Source<TValue>(
            CaseIntakeField<TValue> field,
            ClassifiedCaseDocument document)
            => new(field, document);

        private static MergedCaseIntakeField<TValue> MergeField<TValue>(
            IEnumerable<FieldWithSource<TValue>> sourceFields,
            IEqualityComparer<TValue>? comparer = null)
        {
            comparer ??= EqualityComparer<TValue>.Default;
            var fields = sourceFields.ToArray();
            var valid = fields
                .Where(item =>
                    item.Field.Status == CaseIntakeFieldStatus.Valid &&
                    item.Field.Value is not null)
                .ToArray();

            var groups = valid
                .GroupBy(item => item.Field.Value, comparer)
                .Select(group => new CandidateGroup<TValue>(
                    group.Key,
                    group.Select(item => ToCandidateSource(item)).DistinctBy(source => source.DocumentId).ToArray()))
                .ToArray();

            var primaryGroups = groups
                .Where(group => group.Sources.Any(source =>
                    source.SourceStrength == CaseIntakeSourceStrength.Primary))
                .ToArray();
            var primaryAmbiguous = fields.Any(item =>
                item.Document.SourceStrength == CaseIntakeSourceStrength.Primary &&
                item.Field.Status == CaseIntakeFieldStatus.Ambiguous);
            var fallbackAmbiguous = fields.Any(item =>
                item.Document.SourceStrength == CaseIntakeSourceStrength.Fallback &&
                item.Field.Status == CaseIntakeFieldStatus.Ambiguous);

            CandidateGroup<TValue>? selectedGroup = null;
            CaseIntakeSourceStrength? selectedStrength = null;
            if (!primaryAmbiguous && primaryGroups.Length == 1)
            {
                selectedGroup = primaryGroups[0];
                selectedStrength = CaseIntakeSourceStrength.Primary;
            }
            else if (!primaryAmbiguous && primaryGroups.Length == 0 && !fallbackAmbiguous && groups.Length == 1)
            {
                selectedGroup = groups[0];
                selectedStrength = CaseIntakeSourceStrength.Fallback;
            }

            var hasConflict = groups.Length > 1 || primaryAmbiguous || fallbackAmbiguous;
            var selectedSources = selectedGroup == null || !selectedStrength.HasValue
                ? Array.Empty<CaseIntakeCandidateSource>()
                : selectedGroup.Sources
                    .Where(source => source.SourceStrength == selectedStrength.Value)
                    .ToArray();
            var selectedSource = selectedGroup == null || !selectedStrength.HasValue
                ? null
                : new CaseIntakeSelectedSource(
                    selectedStrength == CaseIntakeSourceStrength.Primary
                        ? CaseIntakeDocumentType.NotificationForm
                        : CaseIntakeDocumentType.DemandForm,
                    selectedStrength.Value,
                    selectedSources);
            var crossValidationSucceeded = selectedGroup != null &&
                selectedGroup.Sources.Select(source => source.DocumentId).Distinct().Count() >= 2;

            var validationStatus = hasConflict
                ? CaseIntakeFieldStatus.Ambiguous
                : selectedGroup != null
                    ? CaseIntakeFieldStatus.Valid
                    : fields.Any(item => item.Field.Status == CaseIntakeFieldStatus.Invalid)
                        ? CaseIntakeFieldStatus.Invalid
                        : CaseIntakeFieldStatus.Missing;

            return new MergedCaseIntakeField<TValue>(
                selectedGroup == null ? default! : selectedGroup.Value,
                selectedSource,
                groups.Select(group => new CaseIntakeValueCandidate<TValue>(group.Value, group.Sources)).ToArray(),
                validationStatus,
                crossValidationSucceeded,
                hasConflict);
        }

        private static CaseIntakeCandidateSource ToCandidateSource<TValue>(
            FieldWithSource<TValue> item)
            => new(
                item.Field.SourceDocumentId,
                item.Field.SourceDocumentName,
                item.Document.BusinessType,
                item.Document.SourceStrength,
                item.Field.SourceLabel);

        private sealed record FieldWithSource<TValue>(
            CaseIntakeField<TValue> Field,
            ClassifiedCaseDocument Document);

        private sealed record CandidateGroup<TValue>(
            TValue Value,
            IReadOnlyList<CaseIntakeCandidateSource> Sources);
    }
}
