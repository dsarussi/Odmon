using System.Text.Json;
using Odmon.Worker.Models;
using Odmon.Worker.Monday;
using Odmon.Worker.OdcanitAccess;
using Odmon.Worker.Services;
using Xunit;

namespace Odmon.Worker.Tests
{
    public class InsurancePartyMappingTests
    {
        private const string LegalPageName = "פרטי תיק נזיקין מליגל";

        [Fact]
        public void ExactLegalUserDataFields_PopulateTrimmedIndependentProperties()
        {
            var odcanitCase = new OdcanitCase
            {
                InsuranceCompany1Name = "existing company 1",
                InsuranceCompany2 = "existing company 2"
            };

            Assert.True(Apply(odcanitCase, "חברת ביטוח תובעת 1", "  Plaintiff Insurance Ltd.  "));
            Assert.True(Apply(odcanitCase, "חברת ביטוח נתבעת 1", "  Defendant Insurance One  "));
            Assert.True(Apply(odcanitCase, "חברת ביטוח נתבעת 2", "\tDefendant Insurance Two\r\n"));

            Assert.Equal("Plaintiff Insurance Ltd.", odcanitCase.PlaintiffInsuranceCompany1);
            Assert.Equal("Defendant Insurance One", odcanitCase.DefendantInsuranceCompany1);
            Assert.Equal("Defendant Insurance Two", odcanitCase.DefendantInsuranceCompany2);
            Assert.Equal("existing company 1", odcanitCase.InsuranceCompany1Name);
            Assert.Equal("existing company 2", odcanitCase.InsuranceCompany2);
        }

        [Fact]
        public void InsurancePartyFields_RequireExactPageAndFieldNames()
        {
            var odcanitCase = new OdcanitCase();

            Assert.False(SqlOdcanitReader.ApplyUserDataField(odcanitCase, new OdcanitUserData
            {
                PageName = "another page",
                FieldName = "חברת ביטוח תובעת 1",
                strData = "must not map"
            }));
            Assert.False(SqlOdcanitReader.ApplyUserDataField(odcanitCase, new OdcanitUserData
            {
                PageName = LegalPageName,
                FieldName = " חברת ביטוח נתבעת 1 ",
                strData = "must not map"
            }));

            Assert.Null(odcanitCase.PlaintiffInsuranceCompany1);
            Assert.Null(odcanitCase.DefendantInsuranceCompany1);
        }

        [Fact]
        public void PopulatedInsurancePartyFields_ProduceConfiguredMondayTextColumns()
        {
            var columns = new Dictionary<string, object>();
            var settings = new MondaySettings();
            var odcanitCase = new OdcanitCase
            {
                PlaintiffInsuranceCompany1 = "Plaintiff Insurance Ltd.",
                DefendantInsuranceCompany1 = "Defendant Insurance One",
                DefendantInsuranceCompany2 = "Defendant Insurance Two"
            };

            SyncService.AddInsurancePartyColumns(columns, settings, odcanitCase);

            using var json = JsonDocument.Parse(JsonSerializer.Serialize(columns));
            Assert.Equal("Plaintiff Insurance Ltd.", json.RootElement.GetProperty("text_mm6yc7kc").GetString());
            Assert.Equal("Defendant Insurance One", json.RootElement.GetProperty("text_mm6yyakt").GetString());
            Assert.Equal("Defendant Insurance Two", json.RootElement.GetProperty("text_mm6y9w3f").GetString());
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public void MissingInsurancePartyFields_AreOmittedFromMondayPayload(string? missingValue)
        {
            var columns = new Dictionary<string, object> { ["text_existing"] = "preserved" };
            var odcanitCase = new OdcanitCase
            {
                PlaintiffInsuranceCompany1 = missingValue,
                DefendantInsuranceCompany1 = missingValue,
                DefendantInsuranceCompany2 = missingValue
            };

            SyncService.AddInsurancePartyColumns(columns, new MondaySettings(), odcanitCase);

            Assert.Single(columns);
            Assert.Equal("preserved", columns["text_existing"]);
        }

        [Fact]
        public void InsurancePartyChange_ChangesContentVersion_WithoutChangingExistingMappings()
        {
            var original = new OdcanitCase
            {
                TikNumber = "1",
                InsuranceCompany1Name = "existing company 1",
                InsuranceCompany2 = "existing company 2",
                PlaintiffInsuranceCompany1 = "original plaintiff company"
            };
            var changed = new OdcanitCase
            {
                TikNumber = "1",
                InsuranceCompany1Name = "existing company 1",
                InsuranceCompany2 = "existing company 2",
                PlaintiffInsuranceCompany1 = "changed plaintiff company"
            };

            Assert.NotEqual(
                SyncService.ComputeContentVersion(original),
                SyncService.ComputeContentVersion(changed));
            Assert.Equal(original.InsuranceCompany1Name, changed.InsuranceCompany1Name);
            Assert.Equal(original.InsuranceCompany2, changed.InsuranceCompany2);
        }

        private static bool Apply(OdcanitCase odcanitCase, string fieldName, string? value)
            => SqlOdcanitReader.ApplyUserDataField(odcanitCase, new OdcanitUserData
            {
                PageName = LegalPageName,
                FieldName = fieldName,
                strData = value
            });
    }
}
