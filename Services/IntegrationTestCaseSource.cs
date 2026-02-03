using System;
using System.Collections.Generic;
using System.Data;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Odmon.Worker.Data;
using Odmon.Worker.Models;

namespace Odmon.Worker.Services
{
    /// <summary>
    /// Test-only case source that reads cases from IntegrationDb test table (e.g. dbo.OdmonTestCases)
    /// instead of Odcanit. Used when Testing.Enable = true.
    /// </summary>
    public class IntegrationTestCaseSource : ICaseSource
    {
        private readonly IntegrationDbContext _integrationDb;
        private readonly IConfiguration _config;
        private readonly ILogger<IntegrationTestCaseSource> _logger;

        public IntegrationTestCaseSource(
            IntegrationDbContext integrationDb,
            IConfiguration config,
            ILogger<IntegrationTestCaseSource> logger)
        {
            _integrationDb = integrationDb;
            _config = config;
            _logger = logger;
        }

        public async Task<List<OdcanitCase>> GetCasesByTikCountersAsync(IEnumerable<int> tikCounters, CancellationToken ct)
        {
            var list = tikCounters != null ? new List<int>(tikCounters) : new List<int>();
            if (list.Count == 0)
            {
                return new List<OdcanitCase>();
            }

            var testingSection = _config.GetSection("Testing");
            var tableName = testingSection.GetValue<string>("TableName") ?? "dbo.OdmonTestCases";

            _logger.LogInformation(
                "Reading test cases from IntegrationDb table {TableName} for TikCounters={TikCounters}",
                tableName,
                string.Join(", ", list));

            var cases = new List<OdcanitCase>();

            var connection = _integrationDb.Database.GetDbConnection();
            var wasClosed = connection.State == ConnectionState.Closed;
            if (wasClosed)
            {
                await connection.OpenAsync(ct);
            }

            try
            {
                await using var command = connection.CreateCommand();
                command.CommandText = BuildSql(tableName, list.Count);
                command.CommandType = CommandType.Text;

                for (int i = 0; i < list.Count; i++)
                {
                    var p = command.CreateParameter();
                    p.ParameterName = $"@p{i}";
                    p.DbType = DbType.Int32;
                    p.Value = list[i];
                    command.Parameters.Add(p);
                }

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

                    var tikCounter = GetInt(raw, "TikCounter") ?? 0;
                    if (tikCounter <= 0)
                    {
                        continue;
                    }

                    var c = new OdcanitCase
                    {
                        TikCounter = tikCounter,
                        TikNumber = GetString(raw, "מספר תיק") ?? string.Empty,
                        ClientName = GetString(raw, "שם לקוח") ?? string.Empty,
                        ClientVisualID = GetString(raw, "מספר לקוח"),
                        ClientPhone = GetString(raw, "טלפון לקוח"),
                        ClientEmail = GetString(raw, "דוא\"ל לקוח"),
                        ClientAddress = GetString(raw, "כתובת לקוח"),
                        ClientTaxId = GetString(raw, "ח.פ. לקוח"),
                        TikName = GetString(raw, "שם תיק"),
                        StatusName = GetString(raw, "סטטוס"),
                        TikType = GetString(raw, "סוג תיק"),
                        tsCreateDate = GetDate(raw, "תאריך פתיחה"),
                        tsModifyDate = GetDate(raw, "תאריך עדכון"),
                        TikCloseDate = GetDate(raw, "תאריך סגירה"),
                        EventDate = GetDate(raw, "תאריך אירוע"),
                        ComplaintReceivedDate = GetDate(raw, "מועד קבלת כתב התביעה"),
                        HozlapTikNumber = GetString(raw, "מספר תיק חוזלא\"פ"),
                        Additional = GetString(raw, "נוסף"),
                        PolicyHolderName = GetString(raw, "שם בעל פוליסה"),
                        PolicyHolderId = GetString(raw, "ת.ז. בעל פוליסה") ?? GetString(raw, "תעודת זהות בעל פוליסה"),
                        PolicyHolderAddress = GetString(raw, "כתובת בעל פוליסה"),
                        PolicyHolderPhone = GetString(raw, "סלולרי בעל פוליסה"),
                        PolicyHolderEmail = GetString(raw, "כתובת דוא\"ל בעל פוליסה"),
                        MainCarNumber = GetString(raw, "מספר רישוי"),
                        SecondCarNumber = GetString(raw, "מספר רישוי נוסף"),
                        DriverName = GetString(raw, "שם נהג"),
                        DriverId = GetString(raw, "תעודת זהות נהג"),
                        DriverPhone = GetString(raw, "סלולרי נהג"),
                        WitnessName = GetString(raw, "שם עד"),
                        PlaintiffName = GetString(raw, "שם תובע"),
                        PlaintiffId = GetString(raw, "ת.ז. תובע"),
                        PlaintiffAddress = GetString(raw, "כתובת תובע"),
                        PlaintiffPhone = GetString(raw, "סלולרי תובע") ?? GetString(raw, "טלפון - תובע"),
                        PlaintiffEmail = GetString(raw, "כתובת דוא\"ל תובע"),
                        DefendantName = GetString(raw, "שם נתבע"),
                        DefendantFax = GetString(raw, "פקס"),
                        ThirdPartyDriverName = GetString(raw, "שם נהג צד ג'"),
                        ThirdPartyDriverId = GetString(raw, "ת.ז. נהג צד ג'"),
                        ThirdPartyCarNumber = GetString(raw, "מספר רישוי רכב ג'") ?? GetString(raw, "מספר רכב צד ג'"),
                        ThirdPartyPhone = GetString(raw, "נייד צד ג'"),
                        ThirdPartyInsurerName = GetString(raw, "חברה מבטחת צד ג'"),
                        ThirdPartyEmployerName = GetString(raw, "שם מעסיק צד ג'"),
                        ThirdPartyEmployerId = GetString(raw, "מספר זהות מעסיק צד ג'"),
                        ThirdPartyEmployerAddress = GetString(raw, "כתובת מעסיק צד ג'"),
                        ThirdPartyLawyerName = GetString(raw, "מיוצג על ידי עו\"ד צד ג'"),
                        ThirdPartyLawyerAddress = GetString(raw, "כתובת עו\"ד צד ג'"),
                        ThirdPartyLawyerPhone = GetString(raw, "טלפון עו\"ד צד ג'"),
                        ThirdPartyLawyerEmail = GetString(raw, "כתובת דוא\"ל עו\"ד צד ג'"),
                        InsuranceCompanyId = GetString(raw, "ח.פ. חברת ביטוח"),
                        InsuranceCompanyAddress = GetString(raw, "כתובת חברת ביטוח"),
                        InsuranceCompanyEmail = GetString(raw, "כתובת דוא\"ל חברת ביטוח"),
                        AdditionalDefendants = GetString(raw, "נתבעים נוספים"),
                        CourtName = GetString(raw, "שם בית משפט"),
                        CourtCity = GetString(raw, "עיר בית משפט"),
                        CourtCaseNumber = GetString(raw, "מספר הליך בית משפט"),
                        JudgeName = GetString(raw, "שם שופט"),
                        HearingDate = GetDate(raw, "תאריך דיון"),
                        HearingTime = GetTimeSpan(raw, "שעה"),
                        AttorneyName = GetString(raw, "שם עורך דין"),
                        DefenseStreet = GetString(raw, "כתובת נתבע") ?? GetString(raw, "מרחוב (הגנה)"),
                        ClaimStreet = GetString(raw, "מרחוב (תביעה)"),
                        CaseFolderId = GetString(raw, "folderID"),
                        Notes = GetString(raw, "גרסאות תביעה"),
                        RequestedClaimAmount = GetDecimal(raw, "סכום תביעה") ?? GetDecimal(raw, "הסעד המבוקש ( סכום תביעה)"),
                        ProvenClaimAmount = GetDecimal(raw, "סכום תביעה מוכח"),
                        JudgmentAmount = GetDecimal(raw, "סכום פסק דין"),
                        AppraiserFeeAmount = GetDecimal(raw, "שכ\"ט שמאי"),
                        DirectDamageAmount = GetDecimal(raw, "נזק ישיר") ?? GetDecimal(raw, "סכום נזק ישיר"),
                        OtherLossesAmount = GetDecimal(raw, "הפסדים"),
                        LossOfValueAmount = GetDecimal(raw, "ירידת ערך"),
                        ResidualValueAmount = GetDecimal(raw, "שווי שרידים"),
                        PlaintiffSideRaw = GetString(raw, "צד תובע"),
                        DefendantSideRaw = GetString(raw, "צד נתבע"),
                        DocumentType = GetString(raw, "סוג מסמך"),
                        RawFields = raw
                    };

                    cases.Add(c);
                }
            }
            finally
            {
                if (wasClosed && connection.State == ConnectionState.Open)
                {
                    await connection.CloseAsync();
                }
            }

            _logger.LogInformation(
                "Loaded {Count} test case(s) from IntegrationDb table {TableName}.",
                cases.Count,
                tableName);

            return cases;
        }

        private static string BuildSql(string tableName, int parameterCount)
        {
            var paramNames = new string[parameterCount];
            for (int i = 0; i < parameterCount; i++)
            {
                paramNames[i] = $"@p{i}";
            }

            return $"SELECT * FROM {tableName} WHERE TikCounter IN ({string.Join(", ", paramNames)})";
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

