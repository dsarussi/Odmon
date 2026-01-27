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
            "אגרהI"
        };

        // Token to UserData FieldName mapping (for tokens that don't match FieldName exactly)
        private static readonly Dictionary<string, string> TokenToFieldNameMap = new Dictionary<string, string>(HebrewComparer)
        {
            ["נזק ישיר"] = "סכום נזק ישיר"
        };

        private readonly OdcanitDbContext _odcanitDb;
        private readonly ILogger<TokenResolverService> _logger;
        private readonly Dictionary<int, Dictionary<string, OdcanitUserData>> _userDataCache = new();

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
            // Defensive check: TikCounter must be valid
            if (tikCounter <= 0)
            {
                _logger.LogWarning(
                    "Cannot resolve token '{Token}' because TikCounter is invalid ({TikCounter}). TikCounter must be a positive integer from OdcanitCase.TikCounter.",
                    token, tikCounter);
                return null;
            }

            // Check ignore list first
            if (IgnoreTokens.Contains(token))
            {
                _logger.LogDebug("Token '{Token}' is in ignore list for TikCounter {TikCounter}", token, tikCounter);
                return string.Empty;
            }

            // Load and cache UserData for this TikCounter if not already loaded
            if (!_userDataCache.TryGetValue(tikCounter, out var fieldMap))
            {
                fieldMap = await LoadUserDataForTikAsync(tikCounter, ct);
                _userDataCache[tikCounter] = fieldMap;
            }

            // Resolve field name (check mapping first, then use token as-is)
            var fieldName = TokenToFieldNameMap.TryGetValue(token, out var mapped)
                ? mapped
                : token;

            // Normalize field name (same logic as SqlOdcanitReader)
            var normalizedFieldName = NormalizeUserFieldName(fieldName);
            if (normalizedFieldName == null)
            {
                _logger.LogDebug("Token '{Token}' normalized to null for TikCounter {TikCounter}", token, tikCounter);
                return null;
            }

            // Look up in cached UserData
            if (!fieldMap.TryGetValue(normalizedFieldName, out var userDataRow))
            {
                _logger.LogDebug("Token '{Token}' (FieldName: '{FieldName}') not found in UserData for TikCounter {TikCounter}", token, fieldName, tikCounter);
                return null;
            }

            // Extract value: prefer numData, fallback to strData
            string? value = null;

            if (userDataRow.numData.HasValue)
            {
                // Format as plain number text (no currency symbols, no decimals if whole number)
                var numValue = Convert.ToDecimal(userDataRow.numData.Value, CultureInfo.InvariantCulture);
                if (numValue == Math.Truncate(numValue))
                {
                    value = ((long)numValue).ToString(CultureInfo.InvariantCulture);
                }
                else
                {
                    value = numValue.ToString(CultureInfo.InvariantCulture);
                }
            }
            else if (!string.IsNullOrWhiteSpace(userDataRow.strData))
            {
                value = userDataRow.strData.Trim();
            }

            if (string.IsNullOrWhiteSpace(value))
            {
                _logger.LogDebug("Token '{Token}' found in UserData for TikCounter {TikCounter} but value is empty", token, tikCounter);
                return null;
            }

            _logger.LogDebug("Resolved token '{Token}' = '{Value}' for TikCounter {TikCounter}", token, value, tikCounter);
            return value;
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

            foreach (var token in tokens)
            {
                var value = await ResolveTokenAsync(tikCounter, token, ct);
                
                if (value == string.Empty)
                {
                    // Explicitly ignored
                    result[token] = string.Empty;
                    ignored++;
                }
                else if (value != null)
                {
                    result[token] = value;
                    resolvedFromOdcanit++;
                }
                else
                {
                    // Not found
                    result[token] = string.Empty;
                    unresolved.Add(token);
                }
            }

            _logger.LogInformation(
                "Token resolution summary for TikCounter {TikCounter}: Resolved from Odcanit={Resolved}, Ignored={Ignored}, Unresolved={Unresolved}. Unresolved tokens: {UnresolvedTokens}",
                tikCounter,
                resolvedFromOdcanit,
                ignored,
                unresolved.Count,
                unresolved.Count > 0 ? string.Join(", ", unresolved) : "none");

            return result;
        }

        /// <summary>
        /// Clears the UserData cache. Call this at the start of each document generation run.
        /// </summary>
        public void ClearCache()
        {
            _userDataCache.Clear();
            _logger.LogDebug("Cleared UserData cache");
        }

        private async Task<Dictionary<string, OdcanitUserData>> LoadUserDataForTikAsync(int tikCounter, CancellationToken ct)
        {
            var userDataRows = await _odcanitDb.UserData
                .AsNoTracking()
                .Where(u => u.TikCounter == tikCounter && u.PageName == LegalUserDataPageName)
                .ToListAsync(ct);

            var fieldMap = new Dictionary<string, OdcanitUserData>(HebrewComparer);

            foreach (var row in userDataRows)
            {
                var normalized = NormalizeUserFieldName(row.FieldName);
                if (normalized != null && !fieldMap.ContainsKey(normalized))
                {
                    fieldMap[normalized] = row;
                }
            }

            _logger.LogDebug("Loaded {Count} UserData rows for TikCounter {TikCounter} (PageName: '{PageName}')", userDataRows.Count, tikCounter, LegalUserDataPageName);
            return fieldMap;
        }

        private static string? NormalizeUserFieldName(string? fieldName)
        {
            if (string.IsNullOrWhiteSpace(fieldName))
            {
                return null;
            }

            // Use string replacement to avoid character encoding issues
            var result = fieldName
                .Replace("\u2019", "'")  // Right single quotation mark -> apostrophe
                .Replace("\u05F4", "\"") // Hebrew punctuation geresh -> double quote
                .Trim();
            
            return result;
        }
    }
}
