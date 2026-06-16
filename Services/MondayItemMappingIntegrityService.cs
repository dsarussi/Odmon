using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Odmon.Worker.Data;
using Odmon.Worker.Models;
using Odmon.Worker.OdcanitAccess;

namespace Odmon.Worker.Services
{
    public sealed class MondayItemMappingIntegrityException : InvalidOperationException
    {
        public MondayItemMappingIntegrityException(string message) : base(message)
        {
        }
    }

    public sealed record MondayItemMappingIntegrityIssue(
        int MappingId,
        long BoardId,
        long MondayItemId,
        int TikCounter,
        string? TikNumber,
        string Reason);

    public sealed class MondayItemMappingIntegrityService
    {
        private readonly IntegrationDbContext _db;
        private readonly IOdcanitReader _odcanitReader;
        private readonly ILogger<MondayItemMappingIntegrityService> _logger;

        public MondayItemMappingIntegrityService(
            IntegrationDbContext db,
            IOdcanitReader odcanitReader,
            ILogger<MondayItemMappingIntegrityService> logger)
        {
            _db = db;
            _odcanitReader = odcanitReader;
            _logger = logger;
        }

        public static void ValidateNewMapping(MondayItemMapping mapping, string source)
        {
            if (mapping.TikCounter <= 0)
            {
                throw new MondayItemMappingIntegrityException(
                    $"{source}: MondayItemMappings.TikCounter must be a real positive Odcanit counter. " +
                    $"Received TikCounter={mapping.TikCounter}, TikNumber={mapping.TikNumber ?? "<null>"}, " +
                    $"BoardId={mapping.BoardId}, MondayItemId={mapping.MondayItemId}.");
            }

            if (string.IsNullOrWhiteSpace(mapping.TikNumber))
            {
                throw new MondayItemMappingIntegrityException(
                    $"{source}: MondayItemMappings.TikNumber is required when linking Monday item {mapping.MondayItemId} " +
                    $"to real Odcanit TikCounter={mapping.TikCounter}.");
            }

            if (mapping.BoardId <= 0)
            {
                throw new MondayItemMappingIntegrityException(
                    $"{source}: MondayItemMappings.BoardId must be set for TikCounter={mapping.TikCounter}.");
            }

            if (mapping.MondayItemId <= 0)
            {
                throw new MondayItemMappingIntegrityException(
                    $"{source}: MondayItemMappings.MondayItemId must be set for TikCounter={mapping.TikCounter}.");
            }
        }

        public static void ValidateMappingMatchesCase(
            MondayItemMapping mapping,
            OdcanitCase odcanitCase,
            long expectedBoardId,
            string source)
        {
            ValidateNewMapping(mapping, source);

            if (mapping.BoardId != expectedBoardId)
            {
                throw new MondayItemMappingIntegrityException(
                    $"{source}: Monday mapping board mismatch for TikCounter={mapping.TikCounter}. " +
                    $"MappingBoardId={mapping.BoardId}, ExpectedBoardId={expectedBoardId}, MondayItemId={mapping.MondayItemId}.");
            }

            if (mapping.TikCounter != odcanitCase.TikCounter)
            {
                throw new MondayItemMappingIntegrityException(
                    $"{source}: Monday mapping TikCounter mismatch. MappingTikCounter={mapping.TikCounter}, " +
                    $"OdcanitTikCounter={odcanitCase.TikCounter}, MappingTikNumber={mapping.TikNumber ?? "<null>"}, " +
                    $"OdcanitTikNumber={odcanitCase.TikNumber ?? "<null>"}, MondayItemId={mapping.MondayItemId}.");
            }

            if (!string.IsNullOrWhiteSpace(odcanitCase.TikNumber) &&
                !string.Equals(mapping.TikNumber?.Trim(), odcanitCase.TikNumber.Trim(), StringComparison.Ordinal))
            {
                throw new MondayItemMappingIntegrityException(
                    $"{source}: Monday mapping TikNumber mismatch for TikCounter={mapping.TikCounter}. " +
                    $"MappingTikNumber={mapping.TikNumber ?? "<null>"}, OdcanitTikNumber={odcanitCase.TikNumber}, " +
                    $"MondayItemId={mapping.MondayItemId}.");
            }
        }

        public async Task<IReadOnlyList<MondayItemMappingIntegrityIssue>> FindIntegrityIssuesAsync(CancellationToken ct)
        {
            var mappings = await _db.MondayItemMappings
                .AsNoTracking()
                .ToListAsync(ct);

            var issues = new List<MondayItemMappingIntegrityIssue>();

            foreach (var mapping in mappings)
            {
                if (mapping.TikCounter <= 0)
                {
                    issues.Add(ToIssue(mapping, "TikCounter is not a real positive Odcanit counter."));
                }

                if (string.IsNullOrWhiteSpace(mapping.TikNumber))
                {
                    issues.Add(ToIssue(mapping, "TikNumber is missing; mapping identity cannot be verified against Odcanit."));
                }

                if (mapping.BoardId <= 0)
                {
                    issues.Add(ToIssue(mapping, "BoardId is missing or invalid."));
                }

                if (mapping.MondayItemId <= 0)
                {
                    issues.Add(ToIssue(mapping, "MondayItemId is missing or invalid."));
                }
            }

            var tikNumbers = mappings
                .Where(m => !string.IsNullOrWhiteSpace(m.TikNumber))
                .Select(m => m.TikNumber!)
                .Distinct(StringComparer.Ordinal)
                .ToList();

            var resolved = await _odcanitReader.ResolveTikNumbersToCountersAsync(tikNumbers, ct);
            foreach (var mapping in mappings.Where(m => !string.IsNullOrWhiteSpace(m.TikNumber)))
            {
                if (!resolved.TryGetValue(mapping.TikNumber!.Trim(), out var realTikCounter))
                {
                    _logger.LogWarning(
                        "MAPPING_INTEGRITY | TikNumber could not be resolved by Odcanit reader; treating as inconclusive, not fatal. MappingId={MappingId}, BoardId={BoardId}, MondayItemId={MondayItemId}, TikCounter={TikCounter}, TikNumber={TikNumber}",
                        mapping.Id,
                        mapping.BoardId,
                        mapping.MondayItemId,
                        mapping.TikCounter,
                        mapping.TikNumber);
                    continue;
                }

                if (mapping.TikCounter != realTikCounter)
                {
                    issues.Add(ToIssue(
                        mapping,
                        $"TikCounter does not match Odcanit for TikNumber. Expected real TikCounter={realTikCounter}."));
                }
            }

            if (issues.Count > 0)
            {
                _logger.LogCritical(
                    "MAPPING_INTEGRITY | Found {IssueCount} invalid MondayItemMappings. Sample={Sample}",
                    issues.Count,
                    string.Join(" | ", issues.Take(10).Select(i =>
                        $"Id={i.MappingId},BoardId={i.BoardId},Item={i.MondayItemId},TikCounter={i.TikCounter},TikNumber={i.TikNumber ?? "<null>"},Reason={i.Reason}")));
            }

            return issues;
        }

        public async Task ThrowIfIntegrityBrokenAsync(CancellationToken ct)
        {
            var issues = await FindIntegrityIssuesAsync(ct);
            if (issues.Count == 0)
            {
                _logger.LogInformation("MAPPING_INTEGRITY | MondayItemMappings integrity check passed.");
                return;
            }

            throw new MondayItemMappingIntegrityException(
                $"MondayItemMappings integrity check failed with {issues.Count} issue(s). " +
                "ODMON will not run while mappings contain fake, invalid, or confirmed-mismatched Odcanit identities.");
        }

        private static MondayItemMappingIntegrityIssue ToIssue(MondayItemMapping mapping, string reason)
        {
            return new MondayItemMappingIntegrityIssue(
                mapping.Id,
                mapping.BoardId,
                mapping.MondayItemId,
                mapping.TikCounter,
                mapping.TikNumber,
                reason);
        }
    }
}
