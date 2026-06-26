using System.Data;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Odmon.Worker.Configuration;
using Odmon.Worker.OdcanitAccess;
using Odmon.Worker.Voicenter;

namespace Odmon.Worker.Services
{
    public sealed class OdcanitVoicenterCasePhoneResolver : IVoicenterCasePhoneResolver
    {
        private readonly OdcanitDbContext _odcanitDb;
        private readonly VoicenterCallSummarySettings _settings;
        private readonly ILogger<OdcanitVoicenterCasePhoneResolver> _logger;

        public string ScopeName => "OdcanitUserDataConfiguredPhoneFields";

        private static readonly string[] BuiltInPhoneFieldNames =
        [
            "\u05e1\u05dc\u05d5\u05dc\u05e8\u05d9 \u05e2\u05d3",
            "\u05e1\u05dc\u05d5\u05dc\u05e8\u05d9 \u05e0\u05d4\u05d2",
            "Driver: phone",
            "\u05e1\u05dc\u05d5\u05dc\u05e8\u05d9 \u05d1\u05e2\u05dc \u05e4\u05d5\u05dc\u05d9\u05e1\u05d4",
            "Policy holder: phone",
            "\u05e0\u05d9\u05d9\u05d3 \u05e6\u05d3 \u05d2'",
            "\u05e0\u05d9\u05d9\u05d3 \u05e6\u05d3 \u05d2",
            "Third-party driver: phone",
            "\u05d8\u05dc\u05e4\u05d5\u05df \u05e2\u05d5\"\u05d3 \u05e6\u05d3 \u05d2'",
            "\u05e1\u05dc\u05d5\u05dc\u05e8\u05d9 \u05ea\u05d5\u05d1\u05e2",
        ];

        public OdcanitVoicenterCasePhoneResolver(
            OdcanitDbContext odcanitDb,
            IOptions<VoicenterCallSummarySettings> settings,
            ILogger<OdcanitVoicenterCasePhoneResolver> logger)
        {
            _odcanitDb = odcanitDb;
            _settings = settings.Value;
            _logger = logger;
        }

        public async Task<IReadOnlyList<CasePhoneMatch>> FindCasesByPhoneAsync(string normalizedPhone, CancellationToken ct)
        {
            var fieldNames = ResolvePhoneFieldNames();
            var matches = new List<CasePhoneMatch>();

            _logger.LogDebug(
                "VOICENTER | Odcanit phone resolver search | Scope={Scope}, Phone={Phone}, FieldCount={FieldCount}",
                ScopeName,
                MaskPhone(normalizedPhone),
                fieldNames.Length);

            var connection = _odcanitDb.Database.GetDbConnection();
            if (connection.State != ConnectionState.Open)
                await connection.OpenAsync(ct);

            await using var command = connection.CreateCommand();
            command.CommandTimeout = 30;

            var paramNames = new string[fieldNames.Length];
            for (int i = 0; i < fieldNames.Length; i++)
            {
                paramNames[i] = $"@fn{i}";
                command.Parameters.Add(new SqlParameter(paramNames[i], SqlDbType.NVarChar, 200) { Value = fieldNames[i] });
            }

            command.CommandText = $@"
SELECT DISTINCT f.TikCounter, f.TikNumber, ud.FieldName, ud.strData
FROM vwExportToOuterSystems_UserData ud WITH (NOLOCK)
INNER JOIN vwExportToOuterSystems_Files f WITH (NOLOCK) ON f.TikCounter = ud.TikCounter
WHERE ud.FieldName IN ({string.Join(", ", paramNames)})
  AND ud.strData IS NOT NULL AND LEN(LTRIM(RTRIM(ud.strData))) > 0";

            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var tikCounter = reader.GetInt32(0);
                var tikNumber = reader.IsDBNull(1) ? "" : reader.GetString(1);
                var fieldName = reader.IsDBNull(2) ? "" : reader.GetString(2);
                var phoneValue = reader.IsDBNull(3) ? "" : reader.GetString(3);

                var normalizedDbPhone = VoicenterCallSummaryService.NormalizeIsraeliPhone(phoneValue);
                if (normalizedDbPhone == null || normalizedDbPhone != normalizedPhone)
                    continue;

                if (!matches.Any(m => m.TikCounter == tikCounter))
                {
                    matches.Add(new CasePhoneMatch
                    {
                        TikCounter = tikCounter,
                        TikNumber = tikNumber,
                        MatchedField = ToMatchedField(fieldName),
                    });
                }
            }

            if (matches.Count == 0)
            {
                _logger.LogInformation(
                    "VOICENTER | Odcanit phone resolver found no case | Scope={Scope}, Phone={Phone}",
                    ScopeName,
                    MaskPhone(normalizedPhone));
            }

            return matches;
        }

        private string[] ResolvePhoneFieldNames()
        {
            var configured = _settings.PhoneFieldNames?
                .Where(f => !string.IsNullOrWhiteSpace(f))
                .Select(f => f.Trim())
                .Distinct(StringComparer.Ordinal)
                .ToArray();

            return configured is { Length: > 0 } ? configured : BuiltInPhoneFieldNames;
        }

        private static string ToMatchedField(string fieldName)
        {
            if (fieldName.Contains("Policy holder", StringComparison.OrdinalIgnoreCase)) return "PolicyHolderPhone";
            if (fieldName.Contains("Driver", StringComparison.OrdinalIgnoreCase)) return "DriverPhone";
            if (fieldName.Contains("Third-party", StringComparison.OrdinalIgnoreCase)) return "ThirdPartyPhone";
            return fieldName;
        }

        private static string MaskPhone(string phone)
        {
            if (phone.Length <= 4) return "****";
            return phone[..^4] + "****";
        }
    }
}
