using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Odmon.Worker.Data;
using Odmon.Worker.Models;

namespace Odmon.Worker.Services
{
    /// <summary>
    /// Test case reader that loads cases from dbo.OdmonTestCases table (IntegrationDb).
    /// Used for end-to-end testing and demos. Enabled via OdmonTestCases:Enable=true.
    /// </summary>
    public class OdmonTestCasesReader : ICaseSource
    {
        private readonly IntegrationDbContext _integrationDb;
        private readonly IConfiguration _config;
        private readonly ILogger<OdmonTestCasesReader> _logger;

        public OdmonTestCasesReader(
            IntegrationDbContext integrationDb,
            IConfiguration config,
            ILogger<OdmonTestCasesReader> logger)
        {
            _integrationDb = integrationDb;
            _config = config;
            _logger = logger;
        }

        public async Task<List<OdcanitCase>> GetCasesByTikCountersAsync(IEnumerable<int> tikCounters, CancellationToken ct)
        {
            var section = _config.GetSection("OdmonTestCases");
            var maxId = section.GetValue<int?>("MaxId");
            var onlyIdsStr = section.GetValue<string?>("OnlyIds");
            List<int>? onlyIds = null;

            if (!string.IsNullOrWhiteSpace(onlyIdsStr))
            {
                onlyIds = onlyIdsStr
                    .Split(',', StringSplitOptions.RemoveEmptyEntries)
                    .Select(s => int.TryParse(s.Trim(), out var id) ? id : (int?)null)
                    .Where(id => id.HasValue)
                    .Select(id => id!.Value)
                    .ToList();
            }

            _logger.LogInformation(
                "Loading test cases from dbo.OdmonTestCases. Filters: MaxId={MaxId}, OnlyIds={OnlyIds}",
                maxId?.ToString() ?? "none",
                onlyIds != null ? string.Join(",", onlyIds) : "none");

            var connection = _integrationDb.Database.GetDbConnection();
            var wasClosed = connection.State == ConnectionState.Closed;
            if (wasClosed)
            {
                await connection.OpenAsync(ct);
            }

            try
            {
                // First, count eligible rows (מספר_תיק not null)
                int totalEligible = await CountEligibleRowsAsync(connection, ct);
                _logger.LogInformation(
                    "dbo.OdmonTestCases: {TotalEligible} eligible rows (מספר_תיק not null)",
                    totalEligible);

                if (totalEligible == 0)
                {
                    throw new InvalidOperationException(
                        "OdmonTestCases:Enable=true but zero eligible rows found in dbo.OdmonTestCases (all rows have NULL מספר_תיק). " +
                        "Please add valid test data or disable OdmonTestCases:Enable.");
                }

                // Build and execute query with filters
                var sql = BuildSelectQuery(maxId, onlyIds);
                await using var command = connection.CreateCommand();
                command.CommandText = sql;
                command.CommandType = CommandType.Text;

                if (maxId.HasValue)
                {
                    var p = command.CreateParameter();
                    p.ParameterName = "@MaxId";
                    p.DbType = DbType.Int32;
                    p.Value = maxId.Value;
                    command.Parameters.Add(p);
                }

                if (onlyIds != null && onlyIds.Count > 0)
                {
                    for (int i = 0; i < onlyIds.Count; i++)
                    {
                        var p = command.CreateParameter();
                        p.ParameterName = $"@Id{i}";
                        p.DbType = DbType.Int32;
                        p.Value = onlyIds[i];
                        command.Parameters.Add(p);
                    }
                }

                var cases = new List<OdcanitCase>();
                await using var reader = await command.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                {
                    var raw = new Dictionary<string, string?>(StringComparer.Ordinal);
                    for (int i = 0; i < reader.FieldCount; i++)
                    {
                        var name = reader.GetName(i);
                        var value = reader.IsDBNull(i) ? null : reader.GetValue(i)?.ToString();
                        raw[name] = value;
                    }

                    var caseNumber = GetString(raw, "מספר_תיק");
                    if (string.IsNullOrWhiteSpace(caseNumber))
                    {
                        // Safety: Skip rows with missing מספר_תיק
                        var id = GetInt(raw, "Id");
                        _logger.LogWarning("Skipping OdmonTestCases row Id={Id} with NULL/empty מספר_תיק", id);
                        continue;
                    }

                    var c = MapRowToCase(raw);
                    cases.Add(c);

                    // Log each loaded row
                    _logger.LogInformation(
                        "Loaded test case: Id={Id}, מספר_תיק={CaseNumber}, סוג_מסמך={DocType}, צד_תובע={PlaintiffSide}, צד_נתבע={DefendantSide}",
                        GetInt(raw, "Id") ?? 0,
                        caseNumber,
                        GetString(raw, "סוג_מסמך") ?? "<null>",
                        GetString(raw, "צד_תובע") ?? "<null>",
                        GetString(raw, "צד_נתבע") ?? "<null>");
                }

                _logger.LogInformation(
                    "Loaded {Count} test case(s) from dbo.OdmonTestCases after filters",
                    cases.Count);

                if (cases.Count == 0)
                {
                    throw new InvalidOperationException(
                        $"OdmonTestCases:Enable=true but zero rows matched filters (MaxId={maxId}, OnlyIds={onlyIdsStr}). " +
                        "Adjust filters or add matching test data.");
                }

                return cases;
            }
            finally
            {
                if (wasClosed && connection.State == ConnectionState.Open)
                {
                    await connection.CloseAsync();
                }
            }
        }

        private static async Task<int> CountEligibleRowsAsync(IDbConnection connection, CancellationToken ct)
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM dbo.OdmonTestCases WHERE מספר_תיק IS NOT NULL AND LTRIM(RTRIM(מספר_תיק)) <> ''";
            command.CommandType = CommandType.Text;
            
            // Cast to DbCommand for async support
            if (command is System.Data.Common.DbCommand dbCommand)
            {
                var result = await dbCommand.ExecuteScalarAsync(ct);
                return result != null && result != DBNull.Value ? Convert.ToInt32(result) : 0;
            }
            
            // Fallback to sync
            var syncResult = command.ExecuteScalar();
            return syncResult != null && syncResult != DBNull.Value ? Convert.ToInt32(syncResult) : 0;
        }

        private static string BuildSelectQuery(int? maxId, List<int>? onlyIds)
        {
            var sql = "SELECT * FROM dbo.OdmonTestCases WHERE מספר_תיק IS NOT NULL AND LTRIM(RTRIM(מספר_תיק)) <> ''";

            if (maxId.HasValue)
            {
                sql += " AND Id <= @MaxId";
            }

            if (onlyIds != null && onlyIds.Count > 0)
            {
                var idParams = string.Join(", ", Enumerable.Range(0, onlyIds.Count).Select(i => $"@Id{i}"));
                sql += $" AND Id IN ({idParams})";
            }

            sql += " ORDER BY Id";
            return sql;
        }

        private static OdcanitCase MapRowToCase(Dictionary<string, string?> raw)
        {
            // Generate synthetic TikCounter from Id (test cases don't have real TikCounters)
            var id = GetInt(raw, "Id") ?? 0;
            var tikCounter = 900000 + id; // Synthetic range starting at 900000

            return new OdcanitCase
            {
                TikCounter = tikCounter,
                TikNumber = GetString(raw, "מספר_תיק") ?? string.Empty,
                TikName = GetString(raw, "שם_תיק"),
                ClientName = GetString(raw, "שם_לקוח"),
                ClientVisualID = GetString(raw, "מספר_לקוח"),
                ClientPhone = GetString(raw, "טלפון_לקוח"),
                ClientEmail = GetString(raw, "דוא\"ל_לקוח"),
                ClientAddress = GetString(raw, "כתובת_לקוח"),
                ClientTaxId = GetString(raw, "ח_פ_לקוח"),
                StatusName = GetString(raw, "סטטוס"),
                TikType = GetString(raw, "סוג_תיק"),
                tsCreateDate = GetDate(raw, "תאריך_פתיחת_תיק") ?? GetDate(raw, "תאריך_פתיחה"),
                tsModifyDate = GetDate(raw, "תאריך_עדכון"),
                TikCloseDate = GetDate(raw, "תאריך_סגירה"),
                EventDate = GetDate(raw, "תאריך_אירוע"),
                ComplaintReceivedDate = GetDate(raw, "מועד_קבלת_כתב_התביעה"),
                HozlapTikNumber = GetString(raw, "מספר_תיק_חוזלא\"פ"),
                Additional = GetString(raw, "נוסף"),
                PolicyHolderName = GetString(raw, "שם_בעל_פוליסה"),
                PolicyHolderId = GetString(raw, "ת_ז_בעל_פוליסה") ?? GetString(raw, "תעודת_זהות_בעל_פוליסה"),
                PolicyHolderAddress = GetString(raw, "כתובת_בעל_פוליסה"),
                PolicyHolderPhone = GetString(raw, "סלולרי_בעל_פוליסה"),
                PolicyHolderEmail = GetString(raw, "כתובת_דוא\"ל_בעל_פוליסה"),
                MainCarNumber = GetString(raw, "מספר_רישוי"),
                SecondCarNumber = GetString(raw, "מספר_רישוי_נוסף"),
                DriverName = GetString(raw, "שם_נהג"),
                DriverId = GetString(raw, "תעודת_זהות_נהג"),
                DriverPhone = GetString(raw, "סלולרי_נהג"),
                WitnessName = GetString(raw, "שם_עד"),
                PlaintiffName = GetString(raw, "שם_תובע"),
                PlaintiffId = GetString(raw, "ת_ז_תובע"),
                PlaintiffAddress = GetString(raw, "כתובת_תובע"),
                PlaintiffPhone = GetString(raw, "סלולרי_תובע") ?? GetString(raw, "טלפון_תובע"),
                PlaintiffEmail = GetString(raw, "כתובת_דוא\"ל_תובע"),
                DefendantName = GetString(raw, "שם_נתבע"),
                DefendantFax = GetString(raw, "פקס"),
                ThirdPartyDriverName = GetString(raw, "שם_נהג_צד_ג'"),
                ThirdPartyDriverId = GetString(raw, "ת_ז_נהג_צד_ג'"),
                ThirdPartyCarNumber = GetString(raw, "מספר_רישוי_רכב_ג'") ?? GetString(raw, "מספר_רכב_צד_ג'"),
                ThirdPartyPhone = GetString(raw, "נייד_צד_ג'"),
                ThirdPartyInsurerName = GetString(raw, "חברה_מבטחת_צד_ג'"),
                ThirdPartyEmployerName = GetString(raw, "שם_מעסיק_צד_ג'"),
                ThirdPartyEmployerId = GetString(raw, "מספר_זהות_מעסיק_צד_ג'"),
                ThirdPartyEmployerAddress = GetString(raw, "כתובת_מעסיק_צד_ג'"),
                ThirdPartyLawyerName = GetString(raw, "מיוצג_על_ידי_עו\"ד_צד_ג'"),
                ThirdPartyLawyerAddress = GetString(raw, "כתובת_עו\"ד_צד_ג'"),
                ThirdPartyLawyerPhone = GetString(raw, "טלפון_עו\"ד_צד_ג'"),
                ThirdPartyLawyerEmail = GetString(raw, "כתובת_דוא\"ל_עו\"ד_צד_ג'"),
                InsuranceCompanyId = GetString(raw, "ח_פ_חברת_ביטוח"),
                InsuranceCompanyAddress = GetString(raw, "כתובת_חברת_ביטוח"),
                InsuranceCompanyEmail = GetString(raw, "כתובת_דוא\"ל_חברת_ביטוח"),
                AdditionalDefendants = GetString(raw, "נתבעים_נוספים"),
                CourtName = GetString(raw, "שם_בית_משפט"),
                CourtCity = GetString(raw, "עיר_בית_משפט"),
                CourtCaseNumber = GetString(raw, "מספר_תיק_בית_משפט") ?? GetString(raw, "מספר_הליך_בית_משפט"),
                JudgeName = GetString(raw, "שם_שופט"),
                HearingDate = GetDate(raw, "תאריך_דיון"),
                HearingTime = GetTimeSpan(raw, "שעה"),
                AttorneyName = GetString(raw, "שם_עורך_דין"),
                DefenseStreet = GetString(raw, "כתובת_נתבע") ?? GetString(raw, "מרחוב_הגנה"),
                ClaimStreet = GetString(raw, "מרחוב_תביעה"),
                CaseFolderId = GetString(raw, "folderID"),
                Notes = GetString(raw, "גרסאות_תביעה"),
                RequestedClaimAmount = GetDecimal(raw, "הסעד_המבוקש_סכום_תביעה") ?? GetDecimal(raw, "סכום_תביעה"),
                ProvenClaimAmount = GetDecimal(raw, "סכום_תביעה_מוכח"),
                JudgmentAmount = GetDecimal(raw, "סכום_פסק_דין"),
                AppraiserFeeAmount = GetDecimal(raw, "שכ\"ט_שמאי"),
                DirectDamageAmount = GetDecimal(raw, "נזק_ישיר") ?? GetDecimal(raw, "סכום_נזק_ישיר"),
                OtherLossesAmount = GetDecimal(raw, "הפסדים"),
                LossOfValueAmount = GetDecimal(raw, "ירידת_ערך"),
                ResidualValueAmount = GetDecimal(raw, "שווי_שרידים"),
                PlaintiffSideRaw = GetString(raw, "צד_תובע"),
                DefendantSideRaw = GetString(raw, "צד_נתבע"),
                DocumentType = GetString(raw, "סוג_מסמך"),
                RawFields = raw
            };
        }

        private static int? GetInt(Dictionary<string, string?> raw, string key)
        {
            if (!raw.TryGetValue(key, out var s) || string.IsNullOrWhiteSpace(s))
            {
                return null;
            }
            if (int.TryParse(s, out var value))
            {
                return value;
            }
            return null;
        }

        private static string? GetString(Dictionary<string, string?> raw, string key)
        {
            return raw.TryGetValue(key, out var s) ? s : null;
        }

        private static DateTime? GetDate(Dictionary<string, string?> raw, string key)
        {
            if (!raw.TryGetValue(key, out var s) || string.IsNullOrWhiteSpace(s))
            {
                return null;
            }
            if (DateTime.TryParse(s, out var value))
            {
                return value;
            }
            return null;
        }

        private static decimal? GetDecimal(Dictionary<string, string?> raw, string key)
        {
            if (!raw.TryGetValue(key, out var s) || string.IsNullOrWhiteSpace(s))
            {
                return null;
            }
            if (decimal.TryParse(s, out var value))
            {
                return value;
            }
            return null;
        }

        private static TimeSpan? GetTimeSpan(Dictionary<string, string?> raw, string key)
        {
            if (!raw.TryGetValue(key, out var s) || string.IsNullOrWhiteSpace(s))
            {
                return null;
            }
            if (TimeSpan.TryParse(s, out var value))
            {
                return value;
            }
            return null;
        }
    }
}
