using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Odmon.Worker.Models;
using Odmon.Worker.OdcanitAccess;

namespace Odmon.Worker.Services
{
    /// <summary>
    /// Resolves document template tokens from Odcanit UserData.
    /// </summary>
    public class TokenResolverService
    {
        private const string LegalUserDataPageName = "פרטי תיק נזיקין מליגל";
        private static readonly StringComparer HebrewComparer = StringComparer.Ordinal;

        // Tokens to ignore completely (return empty string, no lookups)
        private static readonly HashSet<string> IgnoreTokens = new HashSet<string>(HebrewComparer)
        {
            "הסכום הכולל שנספק בפסק דין",
            "שכ\"ט עו\"ד (נגד)",
            "אגרה (נגד)",
            "אגרה",
            "שכר עדות (נגד)",
            "שכ\"ט עו\"ד",
            "שכר עדות",
            "סכום הכולל שנספק בפסק דין (נגד)",
            "התייקרות פוליסה",
            "מ.אגרה I",
            "אגרת בית משפט ( I+II )",
            "אגרהI",
            "אגרת בית משפט"
        };

        // Token to UserData FieldName mapping (for tokens that don't match FieldName exactly)
        private static readonly Dictionary<string, string> TokenToFieldNameMap = new Dictionary<string, string>(HebrewComparer)
        {
            // Amounts
            ["נזק ישיר"] = "סכום נזק ישיר",

            // Defendant address: token {{רחוב נתבע}} backed by UserData "כתובת נתבע"
            ["רחוב נתבע"] = "כתובת נתבע",

            // Third-party car plate: token {{מספר רישוי צד ג}} backed by UserData "מספר רישוי רכב ג'"
            ["מספר רישוי צד ג"] = "מספר רישוי רכב ג'",

            // Accident short circumstances: token {{נסיבות התאונה בקצרה}} backed by UserData "גרסאות תביעה"
            ["נסיבות התאונה בקצרה"] = "גרסאות תביעה"
        };

        private readonly OdcanitDbContext _odcanitDb;
        private readonly ILogger<TokenResolverService> _logger;

        public TokenResolverService(OdcanitDbContext odcanitDb, ILogger<TokenResolverService> logger)
        {
            _odcanitDb = odcanitDb;
            _logger = logger;
        }

        /// <summary>
        /// Resolves a token value from Odcanit UserData for the given TikCounter.
        /// Returns null if token should be ignored or not found.
        /// </summary>
        public async Task<string?> ResolveTokenAsync(int tikCounter, string token, CancellationToken ct)
        {
            // For single-token callers, delegate to ResolveTokensAsync to reuse the same normalization pipeline.
            var dict = await ResolveTokensAsync(tikCounter, new[] { token }, ct);
            return dict.TryGetValue(token, out var value) ? value : null;
        }

        /// <summary>
        /// Resolves multiple tokens for a TikCounter. Returns a dictionary of token -> value.
        /// </summary>
        public async Task<Dictionary<string, string>> ResolveTokensAsync(int tikCounter, IEnumerable<string> tokens, CancellationToken ct)
        {
            var result = new Dictionary<string, string>(HebrewComparer);
            var resolvedFromOdcanit = 0;
            var ignored = 0;
            var unresolved = new List<string>();

            // Defensive check: TikCounter must be valid
            if (tikCounter <= 0)
            {
                _logger.LogWarning(
                    "Cannot resolve tokens because TikCounter is invalid ({TikCounter}). TikCounter must be a positive integer from OdcanitCase.TikCounter.",
                    tikCounter);
                foreach (var token in tokens)
                {
                    result[token] = string.Empty;
                    unresolved.Add(token);
                }
                return result;
            }

            // Load and normalize UserData for this TikCounter ONCE
            var normalizedUserData = await LoadNormalizedUserDataAsync(tikCounter, ct);

            foreach (var token in tokens)
            {
                // Check ignore list first
                if (IgnoreTokens.Contains(token))
                {
                    _logger.LogDebug("Token '{Token}' is in ignore list for TikCounter {TikCounter}", token, tikCounter);
                    result[token] = string.Empty;
                    ignored++;
                    continue;
                }

                // Resolve desired field name (mapping first, then token as-is)
                var desiredFieldName = TokenToFieldNameMap.TryGetValue(token, out var mapped)
                    ? mapped
                    : token;

                var normalizedDesired = NormalizeKey(desiredFieldName);

                string? value = null;

                if (!string.IsNullOrWhiteSpace(normalizedDesired) &&
                    normalizedUserData.TryGetValue(normalizedDesired, out var directValue))
                {
                    value = directValue;
                }
                else if (token == "מספר רישוי צד ג")
                {
                    // Extra safety: try known variants
                    var alt1 = NormalizeKey("מספר רישוי רכב ג");
                    var alt2 = NormalizeKey("מספר רישוי רכב ג'");

                    if (!string.IsNullOrWhiteSpace(alt1) &&
                        normalizedUserData.TryGetValue(alt1, out var altVal1))
                    {
                        value = altVal1;
                    }
                    else if (!string.IsNullOrWhiteSpace(alt2) &&
                             normalizedUserData.TryGetValue(alt2, out var altVal2))
                    {
                        value = altVal2;
                    }
                }

                if (!string.IsNullOrWhiteSpace(value))
                {
                    result[token] = value;
                    resolvedFromOdcanit++;
                }
                else
                {
                    result[token] = string.Empty;
                    unresolved.Add(token);

                    _logger.LogDebug(
                        "Unresolved token for TikCounter {TikCounter}: token='{Token}', desiredField='{DesiredField}', normalized='{NormalizedDesiredField}'",
                        tikCounter,
                        token,
                        desiredFieldName,
                        normalizedDesired ?? "<null>");
                }
            }

            // Targeted debug log for key tokens used in claim templates
            result.TryGetValue("רחוב נתבע", out var defendantAddress);
            result.TryGetValue("מספר רישוי צד ג", out var thirdPartyCarNumber);
            result.TryGetValue("נסיבות התאונה בקצרה", out var accidentCircumstances);

            _logger.LogDebug(
                "Document tokens for TikCounter {TikCounter}: DefendantAddress='{DefendantAddress}', ThirdPartyCarNumber='{ThirdPartyCarNumber}', AccidentCircumstances='{AccidentCircumstances}'",
                tikCounter,
                defendantAddress ?? string.Empty,
                thirdPartyCarNumber ?? string.Empty,
                accidentCircumstances ?? string.Empty);

            _logger.LogInformation(
                "Token resolution summary for TikCounter {TikCounter}: Resolved from Odcanit={Resolved}, Ignored={Ignored}, Unresolved={Unresolved}. Unresolved tokens: {UnresolvedTokens}",
                tikCounter,
                resolvedFromOdcanit,
                ignored,
                unresolved.Count,
                unresolved.Count > 0 ? string.Join(", ", unresolved) : "none");

            return result;
        }

        private async Task<Dictionary<string, string>> LoadNormalizedUserDataAsync(int tikCounter, CancellationToken ct)
        {
            var userDataRows = await _odcanitDb.UserData
                .AsNoTracking()
                .Where(u => u.TikCounter == tikCounter && u.PageName == LegalUserDataPageName)
                .ToListAsync(ct);

            var fieldMap = new Dictionary<string, string>(HebrewComparer);

            foreach (var row in userDataRows)
            {
                var normalizedKey = NormalizeKey(row.FieldName);
                if (string.IsNullOrWhiteSpace(normalizedKey))
                {
                    continue;
                }

                // Prefer first non-empty value for each normalized key
                if (fieldMap.ContainsKey(normalizedKey))
                {
                    continue;
                }

                string? value = null;

                if (row.numData.HasValue)
                {
                    // Format as plain number text (no currency symbols, no decimals if whole number)
                    var numValue = Convert.ToDecimal(row.numData.Value, CultureInfo.InvariantCulture);
                    if (numValue == Math.Truncate(numValue))
                    {
                        value = ((long)numValue).ToString(CultureInfo.InvariantCulture);
                    }
                    else
                    {
                        value = numValue.ToString(CultureInfo.InvariantCulture);
                    }
                }
                else if (!string.IsNullOrWhiteSpace(row.strData))
                {
                    value = row.strData.Trim();
                }

                if (!string.IsNullOrWhiteSpace(value))
                {
                    fieldMap[normalizedKey] = value;
                }
            }

            _logger.LogDebug(
                "Loaded {Count} normalized UserData fields for TikCounter {TikCounter} (PageName: '{PageName}')",
                fieldMap.Count,
                tikCounter,
                LegalUserDataPageName);

            return fieldMap;
        }

        private static string? NormalizeKey(string? input)
        {
            if (string.IsNullOrWhiteSpace(input))
            {
                return null;
            }

            // Replace NBSP with regular space
            var s = input.Replace('\u00A0', ' ');

            // Remove geresh / gershayim (ASCII and Hebrew)
            s = s
                .Replace("'", string.Empty)
                .Replace("\"", string.Empty)
                .Replace("\u05F3", string.Empty) // Hebrew geresh
                .Replace("\u05F4", string.Empty); // Hebrew gershayim

            // Trim and collapse internal whitespace
            s = s.Trim();
            if (s.Length == 0)
            {
                return null;
            }

            var parts = s.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
            return string.Join(" ", parts);
        }
    }
}
