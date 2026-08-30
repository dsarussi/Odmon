using Odmon.Worker.Models;
using Odmon.Worker.OdcanitAccess;

namespace Odmon.Worker.Services
{
    public interface IEmailCasePrimaryResolver
    {
        Task<EmailCaseResolutionSnapshot> ResolveAsync(
            EmailCaseEvidence evidence,
            CancellationToken cancellationToken);
    }

    /// <summary>
    /// Builds the observer-only primary resolution snapshot. It contains no
    /// authority policy and cannot create filing targets.
    /// </summary>
    public sealed class EmailCasePrimaryResolver(IEmailCaseResolutionRepository repository)
        : IEmailCasePrimaryResolver
    {
        public async Task<EmailCaseResolutionSnapshot> ResolveAsync(
            EmailCaseEvidence evidence,
            CancellationToken cancellationToken)
        {
            var tikResults = await repository.ResolveInternalTikNumbersAsync(
                Values(evidence.InternalTikNumbers),
                cancellationToken);
            var claimResults = await repository.ResolveClaimNumbersAsync(
                Values(evidence.ClaimNumbers),
                cancellationToken);
            var courtResults = await repository.ResolveCourtCaseNumbersAsync(
                Values(evidence.CourtCaseNumbers),
                cancellationToken);
            return new EmailCaseResolutionSnapshot(tikResults, claimResults, courtResults);
        }

        private static IEnumerable<string> Values(
            IEnumerable<EmailEvidenceValue> evidence)
            => evidence
                .Select(value => value.NormalizedValue)
                .Distinct(StringComparer.Ordinal);
    }
}
