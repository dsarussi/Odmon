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
            var source = testingSection.GetValue<string>("Source") ?? "IntegrationDbOdmonTestCases";

            _logger.LogInformation(
                "Reading test cases from IntegrationDb table {TableName} (Source={Source}) for TikCounters={TikCounters}",
                tableName,
                source,
                string.Join(", ", list));

            // Check if we're loading from OdmonTestCases (Hebrew columns with underscores)
            var isOdmonTestCases = source.Contains("OdmonTestCases", StringComparison.OrdinalIgnoreCase);
            
            if (isOdmonTestCases)
            {
                return await LoadOdmonTestCasesAsync(tableName, list, ct);
            }

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

        private async Task<List<OdcanitCase>> LoadOdmonTestCasesAsync(string tableName, List<int> tikCounters, CancellationToken ct)
        {
            _logger.LogInformation("Loading OdmonTestCase entities (Hebrew columns with underscores) from {TableName}", tableName);

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
                command.CommandText = BuildSql(tableName, tikCounters.Count);
                command.CommandType = CommandType.Text;

                for (int i = 0; i < tikCounters.Count; i++)
                {
                    var p = command.CreateParameter();
                    p.ParameterName = $"@p{i}";
                    p.DbType = DbType.Int32;
                    p.Value = tikCounters[i];
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

                    var testCase = new OdmonTestCase
                    {
                        Id = GetInt(raw, "Id") ?? 0,
                        TikCounter = GetInt(raw, "TikCounter") ?? 0,
                        מספר_תיק = GetString(raw, "מספר_תיק") ?? string.Empty,
                        שם_תיק = GetString(raw, "שם_תיק"),
                        סטטוס = GetString(raw, "סטטוס"),
                        סוג_תיק = GetString(raw, "סוג_תיק"),
                        שם_לקוח = GetString(raw, "שם_לקוח"),
                        מספר_לקוח = GetString(raw, "מספר_לקוח"),
                        טלפון_לקוח = GetString(raw, "טלפון_לקוח"),
                        דוא_ל_לקוח = GetString(raw, "דוא_ל_לקוח") ?? GetString(raw, "דוא\"ל_לקוח"),
                        כתובת_לקוח = GetString(raw, "כתובת_לקוח"),
                        ח_פ_לקוח = GetString(raw, "ח_פ_לקוח"),
                        תאריך_פתיחת_תיק = GetDate(raw, "תאריך_פתיחת_תיק"),
                        תאריך_פתיחה = GetDate(raw, "תאריך_פתיחה"),
                        תאריך_עדכון = GetDate(raw, "תאריך_עדכון"),
                        תאריך_סגירה = GetDate(raw, "תאריך_סגירה"),
                        תאריך_אירוע = GetDate(raw, "תאריך_אירוע"),
                        מועד_קבלת_כתב_התביעה = GetDate(raw, "מועד_קבלת_כתב_התביעה"),
                        שם_בעל_פוליסה = GetString(raw, "שם_בעל_פוליסה"),
                        ת_ז_בעל_פוליסה = GetString(raw, "ת_ז_בעל_פוליסה"),
                        תעודת_זהות_בעל_פוליסה = GetString(raw, "תעודת_זהות_בעל_פוליסה"),
                        כתובת_בעל_פוליסה = GetString(raw, "כתובת_בעל_פוליסה"),
                        טלפון_בעל_פוליסה = GetString(raw, "טלפון_בעל_פוליסה"),
                        סלולרי_בעל_פוליסה = GetString(raw, "סלולרי_בעל_פוליסה"),
                        כתובת_דוא_ל_בעל_פוליסה = GetString(raw, "כתובת_דוא_ל_בעל_פוליסה") ?? GetString(raw, "כתובת_דוא\"ל_בעל_פוליסה"),
                        מספר_רישוי = GetString(raw, "מספר_רישוי"),
                        מספר_רישוי_נוסף = GetString(raw, "מספר_רישוי_נוסף"),
                        שם_נהג = GetString(raw, "שם_נהג"),
                        תעודת_זהות_נהג = GetString(raw, "תעודת_זהות_נהג"),
                        סלולרי_נהג = GetString(raw, "סלולרי_נהג"),
                        שם_עד = GetString(raw, "שם_עד"),
                        שם_תובע = GetString(raw, "שם_תובע"),
                        ת_ז_תובע = GetString(raw, "ת_ז_תובע"),
                        כתובת_תובע = GetString(raw, "כתובת_תובע"),
                        סלולרי_תובע = GetString(raw, "סלולרי_תובע"),
                        טלפון_תובע = GetString(raw, "טלפון_תובע"),
                        כתובת_דוא_ל_תובע = GetString(raw, "כתובת_דוא_ל_תובע") ?? GetString(raw, "כתובת_דוא\"ל_תובע"),
                        צד_תובע = GetString(raw, "צד_תובע"),
                        שם_נתבע = GetString(raw, "שם_נתבע"),
                        צד_נתבע = GetString(raw, "צד_נתבע"),
                        פקס = GetString(raw, "פקס"),
                        נתבעים_נוספים = GetString(raw, "נתבעים_נוספים"),
                        שם_נהג_צד_ג = GetString(raw, "שם_נהג_צד_ג"),
                        ת_ז_נהג_צד_ג = GetString(raw, "ת_ז_נהג_צד_ג"),
                        מספר_רישוי_רכב_ג = GetString(raw, "מספר_רישוי_רכב_ג"),
                        מספר_רכב_צד_ג = GetString(raw, "מספר_רכב_צד_ג"),
                        נייד_צד_ג = GetString(raw, "נייד_צד_ג"),
                        חברה_מבטחת_צד_ג = GetString(raw, "חברה_מבטחת_צד_ג"),
                        שם_מעסיק_צד_ג = GetString(raw, "שם_מעסיק_צד_ג"),
                        מספר_זהות_מעסיק_צד_ג = GetString(raw, "מספר_זהות_מעסיק_צד_ג"),
                        כתובת_מעסיק_צד_ג = GetString(raw, "כתובת_מעסיק_צד_ג"),
                        מיוצג_על_ידי_עו_ד_צד_ג = GetString(raw, "מיוצג_על_ידי_עו_ד_צד_ג") ?? GetString(raw, "מיוצג_על_ידי_עו\"ד_צד_ג"),
                        כתובת_עו_ד_צד_ג = GetString(raw, "כתובת_עו_ד_צד_ג") ?? GetString(raw, "כתובת_עו\"ד_צד_ג"),
                        טלפון_עו_ד_צד_ג = GetString(raw, "טלפון_עו_ד_צד_ג") ?? GetString(raw, "טלפון_עו\"ד_צד_ג"),
                        כתובת_דוא_ל_עו_ד_צד_ג = GetString(raw, "כתובת_דוא_ל_עו_ד_צד_ג") ?? GetString(raw, "כתובת_דוא\"ל_עו\"ד_צד_ג"),
                        ח_פ_חברת_ביטוח = GetString(raw, "ח_פ_חברת_ביטוח"),
                        כתובת_חברת_ביטוח = GetString(raw, "כתובת_חברת_ביטוח"),
                        כתובת_דוא_ל_חברת_ביטוח = GetString(raw, "כתובת_דוא_ל_חברת_ביטוח") ?? GetString(raw, "כתובת_דוא\"ל_חברת_ביטוח"),
                        שם_בית_משפט = GetString(raw, "שם_בית_משפט"),
                        עיר_בית_משפט = GetString(raw, "עיר_בית_משפט"),
                        מספר_תיק_בית_משפט = GetString(raw, "מספר_תיק_בית_משפט"),
                        מספר_הליך_בית_משפט = GetString(raw, "מספר_הליך_בית_משפט"),
                        שם_שופט = GetString(raw, "שם_שופט"),
                        תאריך_דיון = GetDate(raw, "תאריך_דיון"),
                        שעה = GetTimeSpan(raw, "שעה"),
                        שם_עורך_דין = GetString(raw, "שם_עורך_דין"),
                        כתובת_נתבע = GetString(raw, "כתובת_נתבע"),
                        מרחוב_הגנה = GetString(raw, "מרחוב_הגנה"),
                        מרחוב_תביעה = GetString(raw, "מרחוב_תביעה"),
                        folderID = GetString(raw, "folderID"),
                        נסיבות_התאונה_בקצרה = GetString(raw, "נסיבות_התאונה_בקצרה"),
                        גרסאות_תביעה = GetString(raw, "גרסאות_תביעה"),
                        הסעד_המבוקש_סכום_תביעה = GetDecimal(raw, "הסעד_המבוקש_סכום_תביעה"),
                        סכום_תביעה = GetDecimal(raw, "סכום_תביעה"),
                        סכום_תביעה_מוכח = GetDecimal(raw, "סכום_תביעה_מוכח"),
                        סכום_פסק_דין = GetDecimal(raw, "סכום_פסק_דין"),
                        שכ_ט_שמאי = GetDecimal(raw, "שכ_ט_שמאי") ?? GetDecimal(raw, "שכ\"ט_שמאי"),
                        נזק_ישיר = GetDecimal(raw, "נזק_ישיר"),
                        סכום_נזק_ישיר = GetDecimal(raw, "סכום_נזק_ישיר"),
                        הפסדים = GetDecimal(raw, "הפסדים"),
                        ירידת_ערך = GetDecimal(raw, "ירידת_ערך"),
                        שווי_שרידים = GetDecimal(raw, "שווי_שרידים"),
                        סוג_מסמך = GetString(raw, "סוג_מסמך"),
                        מספר_תיק_חוזלא_פ = GetString(raw, "מספר_תיק_חוזלא_פ") ?? GetString(raw, "מספר_תיק_חוזלא\"פ"),
                        נוסף = GetString(raw, "נוסף")
                    };

                    if (string.IsNullOrWhiteSpace(testCase.מספר_תיק))
                    {
                        _logger.LogWarning("Skipping test case Id={Id} with NULL/empty מספר_תיק", testCase.Id);
                        continue;
                    }

                    // Convert to OdcanitCase for compatibility with existing sync logic
                    var odcanitCase = testCase.ToOdcanitCase();
                    cases.Add(odcanitCase);

                    _logger.LogDebug(
                        "Loaded OdmonTestCase: Id={Id}, TikCounter={TikCounter}, מספר_תיק={CaseNumber}, נזק_ישיר={DirectDamage}, הפסדים={Losses}",
                        testCase.Id,
                        testCase.TikCounter,
                        testCase.מספר_תיק,
                        testCase.נזק_ישיר?.ToString("N2") ?? "null",
                        testCase.הפסדים?.ToString("N2") ?? "null");
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
                "Loaded {Count} OdmonTestCase(s) from {TableName}, converted to OdcanitCase for sync",
                cases.Count,
                tableName);

            return cases;
        }
    }
}

