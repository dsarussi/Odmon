using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Odmon.Worker.OdcanitAccess
{
    public interface IEmailAutomationCaseResolver
    {
        Task<EmailAutomationCaseMatch?> ResolveByCourtCaseNumberAsync(
            string courtCaseNumber,
            CancellationToken cancellationToken);
    }

    public sealed record EmailAutomationCaseMatch(
        int TikCounter,
        string? TikNumber,
        string? ClientVisualId,
        bool IsAmbiguous = false);

    public sealed class EmailAutomationCaseResolver : IEmailAutomationCaseResolver
    {
        private const string CourtCaseNumberFieldName =
            "\u05de\u05e1\u05e4\u05e8 \u05d4\u05dc\u05d9\u05da \u05d1\u05d9\u05ea \u05de\u05e9\u05e4\u05d8";

        private readonly OdcanitDbContext _db;
        private readonly ILogger<EmailAutomationCaseResolver> _logger;

        public EmailAutomationCaseResolver(
            OdcanitDbContext db,
            ILogger<EmailAutomationCaseResolver> logger)
        {
            _db = db;
            _logger = logger;
        }

        public async Task<EmailAutomationCaseMatch?> ResolveByCourtCaseNumberAsync(
            string courtCaseNumber,
            CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(courtCaseNumber))
            {
                return null;
            }

            var normalized = courtCaseNumber.Trim();
            var hozlapCandidates = await _db.HozlapMainData
                .AsNoTracking()
                .Where(x => x.clcCourtNum != null && x.clcCourtNum.Contains(normalized))
                .Select(x => new { x.TikCounter, x.clcCourtNum })
                .ToListAsync(cancellationToken);

            var userDataCandidates = await _db.UserData
                .AsNoTracking()
                .Where(x =>
                    x.FieldName == CourtCaseNumberFieldName &&
                    x.strData != null &&
                    x.strData.Contains(normalized))
                .Select(x => new { x.TikCounter, CourtCaseNumber = x.strData })
                .ToListAsync(cancellationToken);

            var exactTikCounters = hozlapCandidates
                .Where(x => ContainsWholeCaseNumber(x.clcCourtNum, normalized))
                .Select(x => x.TikCounter)
                .Concat(
                    userDataCandidates
                        .Where(x => ContainsWholeCaseNumber(x.CourtCaseNumber, normalized))
                        .Select(x => x.TikCounter))
                .Distinct()
                .ToArray();

            if (exactTikCounters.Length == 0)
            {
                return null;
            }

            if (exactTikCounters.Length > 1)
            {
                _logger.LogWarning(
                    "EMAILAUTOMATION ambiguous case resolution skipped. MatchCount={MatchCount}",
                    exactTikCounters.Length);
                return new EmailAutomationCaseMatch(0, null, null, IsAmbiguous: true);
            }

            var match = await _db.Cases
                .AsNoTracking()
                .Where(x => exactTikCounters.Contains(x.TikCounter))
                .OrderBy(x => x.TikCounter)
                .Select(x => new EmailAutomationCaseMatch(
                    x.TikCounter,
                    x.TikNumber,
                    x.ClientVisualID,
                    false))
                .FirstOrDefaultAsync(cancellationToken);

            return match;
        }

        private static bool ContainsWholeCaseNumber(string? source, string courtCaseNumber)
        {
            if (string.IsNullOrWhiteSpace(source))
            {
                return false;
            }

            return Regex.IsMatch(
                source,
                $@"(?<!\d){Regex.Escape(courtCaseNumber)}(?!\d)",
                RegexOptions.CultureInvariant,
                TimeSpan.FromSeconds(1));
        }
    }
}
