using Microsoft.Extensions.Logging.Abstractions;
using Odmon.Worker.Models;
using Odmon.Worker.OdcanitAccess;
using Odmon.Worker.Services;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;
using Xunit;

namespace Odmon.Worker.Tests
{
    public sealed class CaseIntakeTests
    {
        [Theory]
        [InlineData(
            CaseIntakeDocumentClassifier.DigitalNotificationFormName,
            CaseIntakeDocumentType.NotificationForm,
            CaseIntakeSourceStrength.Primary)]
        [InlineData(
            CaseIntakeDocumentClassifier.CompanyDemandLetterName,
            CaseIntakeDocumentType.DemandForm,
            CaseIntakeSourceStrength.Fallback)]
        [InlineData(
            CaseIntakeDocumentClassifier.PrivatePartyDemandLetterName,
            CaseIntakeDocumentType.DemandForm,
            CaseIntakeSourceStrength.Fallback)]
        public void Classifier_ExactRelevantName_ReturnsBusinessClassification(
            string sourceName,
            CaseIntakeDocumentType expectedType,
            CaseIntakeSourceStrength expectedStrength)
        {
            var classified = CaseIntakeDocumentClassifier.TryClassify(
                sourceName,
                out var type,
                out var strength);

            Assert.True(classified);
            Assert.Equal(expectedType, type);
            Assert.Equal(expectedStrength, strength);
        }

        [Theory]
        [InlineData("מסמך_אחר")]
        [InlineData("טופס_דיווח_דיגיטלי_1.pdf")]
        [InlineData(" טופס_דיווח_דיגיטלי_1")]
        [InlineData("")]
        [InlineData(null)]
        public void Classifier_NonExactOrUnrelatedName_IsIgnored(string? sourceName)
        {
            Assert.False(CaseIntakeDocumentClassifier.TryClassify(
                sourceName,
                out _,
                out _));
        }

        [Theory]
        [InlineData("תירבע", "עברית")]
        [InlineData("םוימ םיכרד תנואת", "תאונת דרכים מיום")]
        [InlineData("רפסמ:העיבת", "מספר:תביעה")]
        [InlineData("רפסמ:העיבת2144533", "מספר:תביעה 2144533")]
        [InlineData("21/07/2025", "21/07/2025")]
        [InlineData("תואמש ימד464.0₪", "דמי שמאות 464.0₪")]
        [InlineData("claims@example.com", "claims@example.com")]
        [InlineData("תירבע English טסקט", "עברית English טקסט")]
        public void HebrewPdfTextNormalizer_NormalizesKnownPdfPigVisualOrder(
            string extracted,
            string expected)
        {
            var normalized = new HebrewPdfTextNormalizer().Normalize(extracted);

            Assert.Equal(expected, normalized);
        }

        [Fact]
        public void HebrewPdfTextNormalizer_PreservesPolicyPunctuationAndNumber()
        {
            var normalized = new HebrewPdfTextNormalizer().Normalize(
                ":הסילופ 'סמ1541786303");

            Assert.Equal(":מס' פוליסה 1541786303", normalized);
        }

        [Fact]
        public void Parser_ReportNumberBecomesClaimNumber_WhenExplicitClaimNumberIsMissing()
        {
            var fields = CreateParser().Parse("מס' דיווח: 123 / 456", CreateDocument());

            Assert.Equal(CaseIntakeFieldStatus.Valid, fields.ClaimNumber.Status);
            Assert.Equal("123/456", fields.ClaimNumber.Value);
            Assert.Equal("מס' דיווח", fields.ClaimNumber.SourceLabel);
            Assert.Equal(1001, fields.ClaimNumber.SourceDocumentId);
            Assert.Equal(
                CaseIntakeDocumentClassifier.DigitalNotificationFormName,
                fields.ClaimNumber.SourceDocumentName);
        }

        [Fact]
        public void Parser_ExplicitClaimNumberTakesPriorityWhenReportNumberMatches()
        {
            var text = """
מספר תביעה: 999-1
מס' דיווח: 999-1
""";

            var fields = CreateParser().Parse(text, CreateDocument());

            Assert.Equal(CaseIntakeFieldStatus.Valid, fields.ClaimNumber.Status);
            Assert.Equal("999-1", fields.ClaimNumber.Value);
            Assert.Equal("מספר תביעה", fields.ClaimNumber.SourceLabel);
        }

        [Fact]
        public void Parser_InvalidExplicitClaimNumberFallsBackToValidReportNumber()
        {
            var text = """
מספר תביעה: לא ידוע
מס' דיווח: 123-4
""";

            var fields = CreateParser().Parse(text, CreateDocument());

            Assert.Equal(CaseIntakeFieldStatus.Valid, fields.ClaimNumber.Status);
            Assert.Equal("123-4", fields.ClaimNumber.Value);
            Assert.Equal("מס' דיווח", fields.ClaimNumber.SourceLabel);
        }

        [Fact]
        public void Parser_ValidExplicitAndReportClaimNumbersThatDifferAreAmbiguous()
        {
            var text = """
מספר תביעה: 999-1
מס' דיווח: 123-4
""";

            var fields = CreateParser().Parse(text, CreateDocument());

            Assert.Equal(CaseIntakeFieldStatus.Ambiguous, fields.ClaimNumber.Status);
            Assert.Null(fields.ClaimNumber.Value);
            Assert.Contains("999-1", fields.ClaimNumber.RawValue, StringComparison.Ordinal);
            Assert.Contains("123-4", fields.ClaimNumber.RawValue, StringComparison.Ordinal);
        }

        [Fact]
        public void Parser_DistinctRepeatedValuesAreAmbiguous()
        {
            var text = """
מספר תביעה: 111
מספר תביעה: 222
""";

            var fields = CreateParser().Parse(text, CreateDocument());

            Assert.Equal(CaseIntakeFieldStatus.Ambiguous, fields.ClaimNumber.Status);
            Assert.Null(fields.ClaimNumber.Value);
            Assert.Contains("111", fields.ClaimNumber.RawValue, StringComparison.Ordinal);
            Assert.Contains("222", fields.ClaimNumber.RawValue, StringComparison.Ordinal);
        }

        [Fact]
        public void IsraeliId_NormalizesAndValidatesChecksum()
        {
            var valid = CaseIntakeFieldValidators.ValidateIdentifierNumber("123-456-782");
            var invalid = CaseIntakeFieldValidators.ValidateIdentifierNumber("123456789");

            Assert.Equal(CaseIntakeFieldStatus.Valid, valid.Status);
            Assert.Equal("123456782", valid.Value);
            Assert.Equal(CaseIntakeFieldStatus.Invalid, invalid.Status);
            Assert.Null(invalid.Value);
        }

        [Theory]
        [InlineData("054-123-4567", "0541234567")]
        [InlineData("+972 54 123 4567", "0541234567")]
        [InlineData("03-123-4567", "031234567")]
        public void Phone_NormalizesPlausibleIsraeliFormats(string raw, string expected)
        {
            var result = CaseIntakeFieldValidators.ValidatePhone(raw);

            Assert.Equal(CaseIntakeFieldStatus.Valid, result.Status);
            Assert.Equal(expected, result.Value);
        }

        [Theory]
        [InlineData("1234")]
        [InlineData("+1 202 555 0123")]
        [InlineData("054-CALL-NOW")]
        public void Phone_RejectsMalformedOrNonIsraeliValues(string raw)
        {
            var result = CaseIntakeFieldValidators.ValidatePhone(raw);

            Assert.Equal(CaseIntakeFieldStatus.Invalid, result.Status);
            Assert.Null(result.Value);
        }

        [Theory]
        [InlineData("12-345-67", "1234567")]
        [InlineData("12 345 678", "12345678")]
        public void VehicleNumber_NormalizesPlausibleValues(string raw, string expected)
        {
            var result = CaseIntakeFieldValidators.ValidateVehicleNumber(raw);

            Assert.Equal(CaseIntakeFieldStatus.Valid, result.Status);
            Assert.Equal(expected, result.Value);
        }

        [Fact]
        public void Parser_MissingFieldsRemainMissingWithDocumentProvenance()
        {
            var fields = CreateParser().Parse("מס' דיווח: 12345", CreateDocument());

            Assert.Equal(CaseIntakeFieldStatus.Missing, fields.EventDate.Status);
            Assert.Null(fields.EventDate.Value);
            Assert.Equal(1001, fields.EventDate.SourceDocumentId);
            Assert.Equal(
                CaseIntakeDocumentClassifier.DigitalNotificationFormName,
                fields.EventDate.SourceDocumentName);
            Assert.Equal(CaseIntakeFieldStatus.Missing, fields.DriverPhone.Status);
            Assert.Equal(CaseIntakeFieldStatus.Missing, fields.ThirdPartyCarNumber.Status);
        }

        [Fact]
        public void Parser_MalformedFieldsRemainInvalidAndDoNotExposeValues()
        {
            var text = """
מספר תביעה: לא ידוע
תאריך אירוע: 31/02/2026
מספר פוליסה: $$$
שם בעל הפוליסה: 12345
תעודת זהות בעל הפוליסה: 123456789
טלפון בעל הפוליסה: 1234
שם הנהג: 98765
תעודת זהות הנהג: ABC
טלפון הנהג: 054-CALL-NOW
מספר רכב: ABC-123
מספר רכב צד ג': 123
""";

            var fields = CreateParser().Parse(text, CreateDocument());

            Assert.All(
                new[]
                {
                    fields.ClaimNumber.Status,
                    fields.EventDate.Status,
                    fields.PolicyNumber.Status,
                    fields.PolicyHolderName.Status,
                    fields.PolicyHolderId.Status,
                    fields.PolicyHolderPhone.Status,
                    fields.DriverName.Status,
                    fields.DriverId.Status,
                    fields.DriverPhone.Status,
                    fields.MainCarNumber.Status,
                    fields.ThirdPartyCarNumber.Status
                },
                status => Assert.Equal(CaseIntakeFieldStatus.Invalid, status));

            Assert.Null(fields.PolicyHolderId.Value);
            Assert.Null(fields.DriverPhone.Value);
            Assert.Null(fields.MainCarNumber.Value);
        }

        [Fact]
        public void Parser_ParsesInitialCanonicalFieldsFromPlainText()
        {
            var text = """
מספר תביעה: 100 / 20
תאריך אירוע: 17/08/2026
מספר פוליסה: POL-7788
שם בעל הפוליסה: ישראל ישראלי
תעודת זהות בעל הפוליסה: 123456782
טלפון בעל הפוליסה: +972-54-123-4567
שם הנהג: נהג לדוגמה
תעודת זהות הנהג: 123456782
טלפון הנהג: 03-123-4567
מספר רכב: 12-345-67
מספר רכב צד ג': 12-345-678
""";

            var fields = CreateParser().Parse(text, CreateDocument());

            Assert.Equal("100/20", fields.ClaimNumber.Value);
            Assert.Equal(new DateOnly(2026, 8, 17), fields.EventDate.Value);
            Assert.Equal("POL-7788", fields.PolicyNumber.Value);
            Assert.Equal("ישראל ישראלי", fields.PolicyHolderName.Value);
            Assert.Equal("123456782", fields.PolicyHolderId.Value);
            Assert.Equal("0541234567", fields.PolicyHolderPhone.Value);
            Assert.Equal("נהג לדוגמה", fields.DriverName.Value);
            Assert.Equal("123456782", fields.DriverId.Value);
            Assert.Equal("031234567", fields.DriverPhone.Value);
            Assert.Equal("1234567", fields.MainCarNumber.Value);
            Assert.Equal("12345678", fields.ThirdPartyCarNumber.Value);
        }

        [Fact]
        public void DemandFilenames_ClassifyAsTheSameEqualPriorityBusinessType()
        {
            Assert.True(CaseIntakeDocumentClassifier.TryClassify(
                CaseIntakeDocumentClassifier.CompanyDemandLetterName,
                out var companyType,
                out var companyStrength));
            Assert.True(CaseIntakeDocumentClassifier.TryClassify(
                CaseIntakeDocumentClassifier.PrivatePartyDemandLetterName,
                out var privateType,
                out var privateStrength));

            Assert.Equal(CaseIntakeDocumentType.DemandForm, companyType);
            Assert.Equal(companyType, privateType);
            Assert.Equal(CaseIntakeSourceStrength.Fallback, companyStrength);
            Assert.Equal(companyStrength, privateStrength);
        }

        [Fact]
        public void DemandParser_ParsesExplicitClaimNumber()
        {
            var fields = CreateDemandParser().Parse(
                "מספר תביעה: 555 / 20",
                CreateDemandDocument());

            Assert.Equal(CaseIntakeFieldStatus.Valid, fields.ClaimNumber.Status);
            Assert.Equal("555/20", fields.ClaimNumber.Value);
            Assert.Equal("מספר תביעה", fields.ClaimNumber.SourceLabel);
        }

        [Fact]
        public void DemandParser_ParsesOurClaimAsCanonicalClaimNumber()
        {
            var fields = CreateDemandParser().Parse(
                "תביעתנו: ABC-7788",
                CreateDemandDocument());

            Assert.Equal(CaseIntakeFieldStatus.Valid, fields.ClaimNumber.Status);
            Assert.Equal("ABC-7788", fields.ClaimNumber.Value);
            Assert.Equal("תביעתנו", fields.ClaimNumber.SourceLabel);
        }

        [Fact]
        public void DemandParser_ParsesPolicyEventDateAndPolicyHolder()
        {
            var text = """
תאריך אירוע: 17/08/2026
מס' פוליסה: POL-12345
שם בעל הפוליסה: ישראל ישראלי
ת.ז: 123456782
""";

            var fields = CreateDemandParser().Parse(text, CreateDemandDocument());

            Assert.Equal(new DateOnly(2026, 8, 17), fields.EventDate.Value);
            Assert.Equal("POL-12345", fields.PolicyNumber.Value);
            Assert.Equal("ישראל ישראלי", fields.PolicyHolderName.Value);
            Assert.Equal("123456782", fields.PolicyHolderId.Value);
        }

        [Fact]
        public void DemandParser_DistinguishesInsuredAndThirdPartyVehiclesByContext()
        {
            var text = """
מספר רישוי רכב המבוטח: 12-345-67
מספר רישוי רכב צד ג': 12-345-678
""";

            var fields = CreateDemandParser().Parse(text, CreateDemandDocument());

            Assert.Equal("1234567", fields.MainCarNumber.Value);
            Assert.Equal("12345678", fields.ThirdPartyCarNumber.Value);
            Assert.Equal("מספר רישוי רכב המבוטח", fields.MainCarNumber.SourceLabel);
            Assert.Equal("מספר רישוי רכב צד ג'", fields.ThirdPartyCarNumber.SourceLabel);
        }

        [Fact]
        public void DemandParser_ParsesAppraiserFeeAndLossOfValueAmounts()
        {
            var text = """
דמי שמאות: 1,234.50 ₪
ירידת ערך: 2.500 ₪
""";

            var fields = CreateDemandParser().Parse(text, CreateDemandDocument());

            Assert.Equal(CaseIntakeFieldStatus.Valid, fields.AppraiserFeeAmount.Status);
            Assert.Equal(1234.50m, fields.AppraiserFeeAmount.Value);
            Assert.Equal(CaseIntakeFieldStatus.Valid, fields.LossOfValueAmount.Status);
            Assert.Equal(2500m, fields.LossOfValueAmount.Value);
        }

        [Fact]
        public void DemandParser_UsesRealDocumentSemanticContexts()
        {
            var text = """
תאריך הדפסה 14/09/2025
תאונת דרכים מיוםהנדון 21/07/2025
מספר:תביעה 2144533
מס' פוליסה 1541786303
הרכב המבוטח בחברתנו מספר רישויבתאריך שבנדון ארעה תאונת דרכים, בין 1763339 ובין הרכב שבבעלותך, מספר רישוי
4193779.
:.דנה בויאנג'ויש להעביר אלינו המחאה על סך הנ"ל לפקודת מבוטחנו
נזק לרכב עפ"י דו"ח שמאי 11797.0₪
דמי שמאות 464.0₪
הרכבירידת ערך 938.0₪
השתתפות עצמית לירידת ערך 704.0-₪
""";

            var fields = CreateDemandParser().Parse(
                text,
                CreateDemandDocument(
                    2214486,
                    CaseIntakeDocumentClassifier.PrivatePartyDemandLetterName));

            Assert.Equal("2144533", fields.ClaimNumber.Value);
            Assert.Equal(new DateOnly(2025, 7, 21), fields.EventDate.Value);
            Assert.NotEqual(new DateOnly(2025, 9, 14), fields.EventDate.Value);
            Assert.Equal("1541786303", fields.PolicyNumber.Value);
            Assert.Equal("דנה בויאנג'ו", fields.PolicyHolderName.Value);
            Assert.Equal("1763339", fields.MainCarNumber.Value);
            Assert.Equal("4193779", fields.ThirdPartyCarNumber.Value);
            Assert.Equal(464.0m, fields.AppraiserFeeAmount.Value);
            Assert.Equal(938.0m, fields.LossOfValueAmount.Value);
            Assert.NotEqual(704.0m, fields.LossOfValueAmount.Value);
            Assert.Contains(
                fields.FinancialCandidates,
                candidate =>
                    candidate.CandidateType == DemandFinancialCandidateType.VehicleDamageAmount &&
                    candidate.Amount.Value == 11797.0m);
            Assert.Contains(
                fields.FinancialCandidates,
                candidate =>
                    candidate.CandidateType == DemandFinancialCandidateType.DeductibleRelatedAmount &&
                    candidate.Amount.Value == -704.0m);
        }

        [Fact]
        public void DemandParser_DoesNotAssignGenericVehicleNumberWithoutRoleContext()
        {
            var fields = CreateDemandParser().Parse(
                "מספר רישוי: 4193779",
                CreateDemandDocument());

            Assert.Equal(CaseIntakeFieldStatus.Missing, fields.MainCarNumber.Status);
            Assert.Equal(CaseIntakeFieldStatus.Missing, fields.ThirdPartyCarNumber.Status);
        }

        [Fact]
        public void DemandParser_PreservesTrailingNegativeSignOnFinancialCandidate()
        {
            var fields = CreateDemandParser().Parse(
                "ניכוי השתתפות עצמית: 1650.0-₪",
                CreateDemandDocument());

            var candidates = fields.FinancialCandidates
                .Where(value =>
                    value.CandidateType == DemandFinancialCandidateType.DeductibleRelatedAmount)
                .ToArray();
            Assert.NotEmpty(candidates);
            Assert.All(candidates, candidate =>
            {
                Assert.Equal(CaseIntakeFieldStatus.Valid, candidate.Amount.Status);
                Assert.Equal(-1650.0m, candidate.Amount.Value);
                Assert.Contains("-", candidate.Amount.RawValue, StringComparison.Ordinal);
            });
        }

        [Fact]
        public void DemandParser_ReturnsUnmappedFinancialCandidatesWithExplicitKinds()
        {
            var text = """
נזק לרכב עפ"י דו"ח שמאי: 12,000 ₪
סה"כ דרישה: 13,500 ₪
השתתפות עצמית: 500 ₪
""";

            var fields = CreateDemandParser().Parse(text, CreateDemandDocument());

            Assert.Collection(
                fields.FinancialCandidates.OrderBy(candidate => candidate.CandidateType),
                candidate =>
                {
                    Assert.Equal(DemandFinancialCandidateType.VehicleDamageAmount, candidate.CandidateType);
                    Assert.Equal(12000m, candidate.Amount.Value);
                },
                candidate =>
                {
                    Assert.Equal(DemandFinancialCandidateType.TotalDemandAmount, candidate.CandidateType);
                    Assert.Equal(13500m, candidate.Amount.Value);
                },
                candidate =>
                {
                    Assert.Equal(DemandFinancialCandidateType.DeductibleRelatedAmount, candidate.CandidateType);
                    Assert.Equal(500m, candidate.Amount.Value);
                });
        }

        [Fact]
        public void Merger_NotificationAndDemandSameValue_IsCrossValidated()
        {
            var notification = CreateNotificationResult("מספר תביעה: 12345", 3001);
            var demand = CreateDemandResult("תביעתנו: 12345", 4001);

            var merged = CreateMerger().Merge([notification], [demand]).ClaimNumber;

            Assert.Equal("12345", merged.SelectedValue);
            Assert.Equal(CaseIntakeDocumentType.NotificationForm, merged.SelectedSource!.BusinessType);
            Assert.Equal(CaseIntakeSourceStrength.Primary, merged.SelectedSource.SourceStrength);
            Assert.True(merged.CrossValidationSucceeded);
            Assert.False(merged.HasConflict);
            var candidate = Assert.Single(merged.ValidCandidates);
            Assert.Equal(2, candidate.Sources.Count);
        }

        [Fact]
        public void Merger_NotificationMissing_UsesValidDemandFallback()
        {
            var notification = CreateNotificationResult("תאריך אירוע: 17/08/2026", 3001);
            var demand = CreateDemandResult("תביעתנו: 12345", 4001);

            var merged = CreateMerger().Merge([notification], [demand]).ClaimNumber;

            Assert.Equal("12345", merged.SelectedValue);
            Assert.Equal(CaseIntakeDocumentType.DemandForm, merged.SelectedSource!.BusinessType);
            Assert.Equal(CaseIntakeSourceStrength.Fallback, merged.SelectedSource.SourceStrength);
            Assert.False(merged.HasConflict);
        }

        [Fact]
        public void Merger_NotificationInvalid_UsesValidDemandFallback()
        {
            var notification = CreateNotificationResult("מספר תביעה: לא ידוע", 3001);
            var demand = CreateDemandResult("תביעתנו: 12345", 4001);

            var merged = CreateMerger().Merge([notification], [demand]).ClaimNumber;

            Assert.Equal("12345", merged.SelectedValue);
            Assert.Equal(CaseIntakeSourceStrength.Fallback, merged.SelectedSource!.SourceStrength);
            Assert.Equal(CaseIntakeFieldStatus.Valid, merged.ValidationStatus);
            Assert.False(merged.HasConflict);
        }

        [Fact]
        public void Merger_NotificationValidAndDemandDifferent_SelectsNotificationAndExposesConflict()
        {
            var notification = CreateNotificationResult("מספר תביעה: 111", 3001);
            var demand = CreateDemandResult("תביעתנו: 222", 4001);

            var merged = CreateMerger().Merge([notification], [demand]).ClaimNumber;

            Assert.Equal("111", merged.SelectedValue);
            Assert.Equal(CaseIntakeDocumentType.NotificationForm, merged.SelectedSource!.BusinessType);
            Assert.Equal(CaseIntakeFieldStatus.Ambiguous, merged.ValidationStatus);
            Assert.True(merged.HasConflict);
            Assert.Equal(2, merged.ValidCandidates.Count);
            Assert.Contains(merged.ValidCandidates, candidate => candidate.Value == "222");
        }

        [Fact]
        public void Merger_TwoDemandFormsWithSameValue_SelectsSharedFallbackValue()
        {
            var companyDemand = CreateDemandResult(
                "מספר תביעה: 12345",
                4001,
                CaseIntakeDocumentClassifier.CompanyDemandLetterName);
            var privateDemand = CreateDemandResult(
                "תביעתנו: 12345",
                4002,
                CaseIntakeDocumentClassifier.PrivatePartyDemandLetterName);

            var merged = CreateMerger().Merge([], [companyDemand, privateDemand]).ClaimNumber;

            Assert.Equal("12345", merged.SelectedValue);
            Assert.Equal(CaseIntakeDocumentType.DemandForm, merged.SelectedSource!.BusinessType);
            Assert.Equal(2, merged.SelectedSource.SupportingDocuments.Count);
            Assert.True(merged.CrossValidationSucceeded);
            Assert.False(merged.HasConflict);
            Assert.Single(merged.ValidCandidates);
        }

        [Fact]
        public void Merger_TwoDemandFormsWithDifferentValues_PreservesConflictWithoutWinner()
        {
            var companyDemand = CreateDemandResult(
                "מספר תביעה: 111",
                4001,
                CaseIntakeDocumentClassifier.CompanyDemandLetterName);
            var privateDemand = CreateDemandResult(
                "תביעתנו: 222",
                4002,
                CaseIntakeDocumentClassifier.PrivatePartyDemandLetterName);

            var merged = CreateMerger().Merge([], [companyDemand, privateDemand]).ClaimNumber;

            Assert.Null(merged.SelectedValue);
            Assert.Null(merged.SelectedSource);
            Assert.Equal(CaseIntakeFieldStatus.Ambiguous, merged.ValidationStatus);
            Assert.True(merged.HasConflict);
            Assert.Equal(2, merged.ValidCandidates.Count);
        }

        [Fact]
        public async Task ReadService_IgnoresUnrelatedDocuments_AndParsesApprovedNotificationAndDemandForms()
        {
            var unrelated = CreateDocument(2001, "מסמך_אחר", @"\\server\docs\other.pdf");
            var notification = CreateDocument(2002, path: @"\\server\docs\notification.pdf");
            var demand = CreateDocument(
                2003,
                CaseIntakeDocumentClassifier.CompanyDemandLetterName,
                @"\\server\docs\demand.pdf");
            var reader = new FakeDocumentReader([unrelated, notification, demand]);
            var extractor = new FakePdfTextExtractor(new Dictionary<string, string>
            {
                [notification.Path!] = "חוויד 'סמ: 12345",
                [demand.Path!] = "ונתעיבת: 12345"
            });
            var service = new CaseIntakeReadService(
                reader,
                extractor,
                new HebrewPdfTextNormalizer(),
                CreateParser(),
                CreateDemandParser(),
                CreateMerger(),
                NullLogger<CaseIntakeReadService>.Instance);

            var result = await service.ReadAsync(42, CancellationToken.None);

            Assert.Equal(2, result.Documents.Count);
            Assert.DoesNotContain(result.Documents, item => item.Source.Name == unrelated.Name);
            Assert.Single(result.NotificationForms);
            Assert.Single(result.DemandForms);
            Assert.Equal([notification.Path, demand.Path], extractor.OpenedPaths);
            Assert.Equal("12345", result.MergedFields.ClaimNumber.SelectedValue);
            Assert.True(result.MergedFields.ClaimNumber.CrossValidationSucceeded);
            Assert.Contains(
                result.Documents,
                item => item.Source.Id == demand.Id &&
                        item.BusinessType == CaseIntakeDocumentType.DemandForm &&
                        item.SourceStrength == CaseIntakeSourceStrength.Fallback);
        }

        [Fact]
        public async Task ReadService_NormalizesRealDemandPatternBeforeParsing()
        {
            var demand = CreateDemandDocument(
                40514,
                CaseIntakeDocumentClassifier.PrivatePartyDemandLetterName);
            var rawPdfText = """
רפסמ:העיבת2144533
םוימ םיכרד תנואת21/07/2025
:הסילופ 'סמ1541786303
הסילופה לעב םש:ו'גנאיוב הנד
חטובמה בכר יושיר רפסמ1763339
'ג דצ בכר יושיר רפסמ4193779
תואמש ימד464.0₪
ךרע תדירי938.0₪
""";
            var reader = new FakeDocumentReader([demand]);
            var extractor = new FakePdfTextExtractor(new Dictionary<string, string>
            {
                [demand.Path!] = rawPdfText
            });
            var service = new CaseIntakeReadService(
                reader,
                extractor,
                new HebrewPdfTextNormalizer(),
                CreateParser(),
                CreateDemandParser(),
                CreateMerger(),
                NullLogger<CaseIntakeReadService>.Instance);

            var result = await service.ReadAsync(40514, CancellationToken.None);

            var fields = Assert.Single(result.DemandForms).Fields!;
            Assert.Equal("2144533", fields.ClaimNumber.Value);
            Assert.Equal(new DateOnly(2025, 7, 21), fields.EventDate.Value);
            Assert.Equal("1541786303", fields.PolicyNumber.Value);
            Assert.Equal("דנה בויאנג'ו", fields.PolicyHolderName.Value);
            Assert.Equal("1763339", fields.MainCarNumber.Value);
            Assert.Equal("4193779", fields.ThirdPartyCarNumber.Value);
            Assert.Equal(464.0m, fields.AppraiserFeeAmount.Value);
            Assert.Equal(938.0m, fields.LossOfValueAmount.Value);
            Assert.Equal(CaseIntakeFieldStatus.Missing, fields.PolicyHolderId.Status);
        }

        [Fact]
        public async Task PdfTextExtractor_ReadsAnExistingPdfTextLayer()
        {
            var filePath = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"odmon-case-intake-{Guid.NewGuid():N}.pdf");

            try
            {
                var builder = new PdfDocumentBuilder();
                var font = builder.AddStandard14Font(Standard14Font.Helvetica);
                var page = builder.AddPage(PageSize.A4);
                page.AddText("Claim 12345", 12, new PdfPoint(25, 700), font);
                await File.WriteAllBytesAsync(filePath, builder.Build());

                var text = await new PdfTextExtractor().ExtractTextAsync(
                    filePath,
                    CancellationToken.None);

                Assert.Contains("Claim 12345", text, StringComparison.Ordinal);
            }
            finally
            {
                if (File.Exists(filePath))
                {
                    File.Delete(filePath);
                }
            }
        }

        [Fact]
        public void DiscoverySql_IsReadOnlyAndRestrictsTheThreeExactNames()
        {
            var sql = SqlCaseIntakeDocumentReader.RelevantDocumentsCommandText;

            Assert.Contains("[dbo].[vwExportToOuterSystems_Documents]", sql, StringComparison.Ordinal);
            Assert.Contains("[TikCounter] = @tikCounter", sql, StringComparison.Ordinal);
            Assert.Contains(
                "[Name] IN (@notificationFormName, @companyDemandName, @privateDemandName)",
                sql,
                StringComparison.Ordinal);
            Assert.DoesNotContain("UPDATE", sql, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("INSERT", sql, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("DELETE", sql, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("EXEC", sql, StringComparison.OrdinalIgnoreCase);
        }

        private static DigitalNotificationFormParser CreateParser() => new();

        private static DemandFormParser CreateDemandParser() => new();

        private static CaseIntakeResultMerger CreateMerger() => new();

        private static NotificationFormReadResult CreateNotificationResult(string text, long documentId)
        {
            var document = CreateDocument(documentId, path: $@"\\server\docs\notification-{documentId}.pdf");
            var classified = new ClassifiedCaseDocument(
                document,
                CaseIntakeDocumentType.NotificationForm,
                CaseIntakeSourceStrength.Primary);
            return new NotificationFormReadResult(
                classified,
                CaseIntakeDocumentReadStatus.Parsed,
                CreateParser().Parse(text, document),
                null);
        }

        private static DemandFormReadResult CreateDemandResult(
            string text,
            long documentId,
            string name = CaseIntakeDocumentClassifier.CompanyDemandLetterName)
        {
            var document = CreateDemandDocument(documentId, name);
            var classified = new ClassifiedCaseDocument(
                document,
                CaseIntakeDocumentType.DemandForm,
                CaseIntakeSourceStrength.Fallback);
            return new DemandFormReadResult(
                classified,
                CaseIntakeDocumentReadStatus.Parsed,
                CreateDemandParser().Parse(text, document),
                null);
        }

        private static OdcanitCaseDocument CreateDocument(
            long id = 1001,
            string name = CaseIntakeDocumentClassifier.DigitalNotificationFormName,
            string? path = @"\\server\docs\notification.pdf")
            => new(
                id,
                name,
                path,
                42,
                "1/42",
                "1",
                "PDF",
                new DateTime(2026, 8, 17));

        private static OdcanitCaseDocument CreateDemandDocument(
            long id = 2001,
            string name = CaseIntakeDocumentClassifier.CompanyDemandLetterName)
            => CreateDocument(id, name, $@"\\server\docs\demand-{id}.pdf");

        private sealed class FakeDocumentReader : ICaseIntakeDocumentReader
        {
            private readonly IReadOnlyList<OdcanitCaseDocument> _documents;

            public FakeDocumentReader(IReadOnlyList<OdcanitCaseDocument> documents)
            {
                _documents = documents;
            }

            public Task<IReadOnlyList<OdcanitCaseDocument>> GetRelevantDocumentsAsync(
                int tikCounter,
                CancellationToken ct)
                => Task.FromResult(_documents);
        }

        private sealed class FakePdfTextExtractor : IPdfTextExtractor
        {
            private readonly IReadOnlyDictionary<string, string> _textByPath;

            public FakePdfTextExtractor(IReadOnlyDictionary<string, string> textByPath)
            {
                _textByPath = textByPath;
            }

            public List<string> OpenedPaths { get; } = [];

            public Task<string> ExtractTextAsync(string filePath, CancellationToken ct)
            {
                OpenedPaths.Add(filePath);
                return Task.FromResult(_textByPath[filePath]);
            }
        }
    }
}
