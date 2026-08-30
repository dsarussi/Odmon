using Odmon.Worker.Models;
using Odmon.Worker.Services;
using Xunit;

namespace Odmon.Worker.Tests
{
    public sealed class EmailCaseEvidenceExtractorTests
    {
        private readonly EmailCaseEvidenceExtractor _extractor = new();

        [Fact]
        public void ExtractsTikNumbersFromSubjectAndBody()
        {
            var evidence = Extract("עדכון 9/1984", "טיפול בתיק 7/1236002");

            Assert.Collection(
                evidence.InternalTikNumbers.OrderBy(value => value.Source),
                value => AssertEvidence(
                    value,
                    EmailEvidenceType.InternalTikNumber,
                    "9/1984",
                    EmailEvidenceSource.Subject,
                    EmailEvidenceExtractionKind.StructuredPattern),
                value => AssertEvidence(
                    value,
                    EmailEvidenceType.InternalTikNumber,
                    "7/1236002",
                    EmailEvidenceSource.Body,
                    EmailEvidenceExtractionKind.StructuredPattern));
        }

        [Fact]
        public void PreservesMultipleTikNumbers()
        {
            var evidence = Extract("תיקים 9/1984, 253/248 ו-7000/355", null);

            Assert.Equal(
                ["9/1984", "253/248", "7000/355"],
                evidence.InternalTikNumbers.Select(value => value.NormalizedValue));
        }

        [Fact]
        public void DeduplicatesWithinSourceButRetainsCrossSourceProvenance()
        {
            var evidence = Extract("9/1984 וגם 9/1984", "9/1984");

            Assert.Equal(2, evidence.InternalTikNumbers.Count);
            Assert.Equal(
                [EmailEvidenceSource.Subject, EmailEvidenceSource.Body],
                evidence.InternalTikNumbers.Select(value => value.Source));
            Assert.All(
                evidence.InternalTikNumbers,
                value => Assert.Equal("9/1984", value.NormalizedValue));
        }

        [Fact]
        public void CapturesCompleteDottedVisualIdWithoutPartialTik()
        {
            var evidence = Extract("עדכון 94/1.7076.", null);

            var value = Assert.Single(evidence.InternalTikNumbers);
            Assert.Equal("94/1.7076", value.NormalizedValue);
            Assert.DoesNotContain(
                evidence.InternalTikNumbers,
                candidate => candidate.NormalizedValue == "94/1");
        }

        [Theory]
        [InlineData("03/10/2022")]
        [InlineData("15/9/27")]
        [InlineData("900/10179/1")]
        public void DoesNotExtractDateOrMultiSlashNoiseAsTik(string input)
        {
            Assert.Empty(Extract(input, null).InternalTikNumbers);
        }

        [Theory]
        [InlineData("מספר תביעה: CLM-100", "CLM-100")]
        [InlineData("תיק תביעה מספר 200_ABC", "200_ABC")]
        [InlineData("זיהוי נוסף - 300/XYZ", "300/XYZ")]
        [InlineData("מס׳   תביעה ： 400.22", "400.22")]
        public void ExtractsOnlyExplicitClaimNumberVariants(string input, string expected)
        {
            var value = Assert.Single(Extract(input, null).ClaimNumbers);

            AssertEvidence(
                value,
                EmailEvidenceType.ClaimNumber,
                expected,
                EmailEvidenceSource.Subject,
                EmailEvidenceExtractionKind.ExplicitLabel);
        }

        [Theory]
        [InlineData("מספר הליך: 12345-01-26", "12345-01-26")]
        [InlineData("מספר תיק בית משפט 54321-02-25", "54321-02-25")]
        [InlineData("מספר הליך בית משפט: 777-03-24", "777-03-24")]
        [InlineData("תיק בית משפט מס׳ 888-04-23", "888-04-23")]
        public void ExtractsExplicitCourtCaseNumberVariants(string input, string expected)
        {
            var value = Assert.Single(Extract(null, input).CourtCaseNumbers);

            AssertEvidence(
                value,
                EmailEvidenceType.CourtCaseNumber,
                expected,
                EmailEvidenceSource.Body,
                EmailEvidenceExtractionKind.ExplicitLabel);
        }

        [Fact]
        public void ExtractsUnlabeledCourtCaseNumberAsEntireSubject()
        {
            var value = Assert.Single(Extract("8069-09-24", null).CourtCaseNumbers);

            AssertEvidence(
                value,
                EmailEvidenceType.CourtCaseNumber,
                "8069-09-24",
                EmailEvidenceSource.Subject,
                EmailEvidenceExtractionKind.StructuredPattern);
        }

        [Fact]
        public void ExtractsUnlabeledCourtCaseNumberEmbeddedInNaturalSubjectText()
        {
            var value = Assert.Single(Extract(
                "שים לב ל-8069-09-24 צריך בו התייעצות בהקדם",
                null).CourtCaseNumbers);

            AssertEvidence(
                value,
                EmailEvidenceType.CourtCaseNumber,
                "8069-09-24",
                EmailEvidenceSource.Subject,
                EmailEvidenceExtractionKind.StructuredPattern);
        }

        [Fact]
        public void ExtractsUnlabeledCourtCaseNumberFromBodyWithBodyProvenance()
        {
            var value = Assert.Single(Extract(
                null,
                "עדכון לגבי 8069-09-24 בהמשך היום").CourtCaseNumbers);

            AssertEvidence(
                value,
                EmailEvidenceType.CourtCaseNumber,
                "8069-09-24",
                EmailEvidenceSource.Body,
                EmailEvidenceExtractionKind.StructuredPattern);
        }

        [Theory]
        [InlineData("2024-09-08")]
        [InlineData("08-09-24")]
        [InlineData("1234-13-24")]
        [InlineData("1234567-09-24")]
        [InlineData("1-8069-09-24")]
        [InlineData("8069-09-24-1")]
        [InlineData("ABC-09-24")]
        [InlineData("12-345-67")]
        public void DoesNotExtractDateLikeOrRandomHyphenatedValuesAsCourtCaseNumber(
            string input)
        {
            Assert.Empty(Extract(input, null).CourtCaseNumbers);
        }

        [Fact]
        public void ExplicitCourtLabelWinsOverStructuredDuplicate()
        {
            var value = Assert.Single(Extract(
                "מספר הליך: 8069-09-24",
                null).CourtCaseNumbers);

            Assert.Equal(EmailEvidenceExtractionKind.ExplicitLabel, value.ExtractionKind);
        }

        [Theory]
        [InlineData("מספר רכב: 12-345-67", "1234567")]
        [InlineData("מספר רישוי. 123 45 678", "12345678")]
        [InlineData("מס׳ רכב：98-765-43", "9876543")]
        public void ExtractsAndConservativelyNormalizesVehicleNumber(
            string input,
            string expected)
        {
            var value = Assert.Single(Extract(null, input).VehicleNumbers);

            Assert.Equal(expected, value.NormalizedValue);
            Assert.Equal(EmailEvidenceExtractionKind.ExplicitLabel, value.ExtractionKind);
        }

        [Theory]
        [InlineData("תאריך אירוע: 30/08/2026", "2026-08-30")]
        [InlineData("תאריך האירוע 2026-08-29", "2026-08-29")]
        public void ExtractsAndNormalizesExplicitEventDate(string input, string expected)
        {
            var value = Assert.Single(Extract(null, input).EventDates);

            Assert.Equal(expected, value.NormalizedValue);
            Assert.Equal(EmailEvidenceExtractionKind.ExplicitLabel, value.ExtractionKind);
        }

        [Theory]
        [InlineData("שם מבוטח: ישראל בדיקה", "ישראל בדיקה")]
        [InlineData("שם מבוטחנו - חברה לדוגמה", "חברה לדוגמה")]
        [InlineData("שם בעל פוליסה: בעלת פוליסה", "בעלת פוליסה")]
        [InlineData("Insured name: Synthetic Person", "Synthetic Person")]
        public void ExtractsExplicitInsuredName(string input, string expected)
        {
            var value = Assert.Single(Extract(null, input).InsuredNames);

            Assert.Equal(expected, value.NormalizedValue);
            Assert.Equal(EmailEvidenceExtractionKind.ExplicitLabel, value.ExtractionKind);
        }

        [Fact]
        public void ExtractsMultipleValuesOfSameType()
        {
            var evidence = Extract(
                null,
                "מספר תביעה: CLM-1\r\nמספר תביעה: CLM-2");

            Assert.Equal(
                ["CLM-1", "CLM-2"],
                evidence.ClaimNumbers.Select(value => value.NormalizedValue));
        }

        [Fact]
        public void ExtractsFromHtmlNormalizedBodyInput()
        {
            var normalizedBody = EmailFilingService.NormalizeBodyForDetection(
                "<p>מספר תביעה:</p><strong>CLM-HTML</strong>",
                "html");

            var value = Assert.Single(Extract(null, normalizedBody).ClaimNumbers);

            Assert.Equal("CLM-HTML", value.NormalizedValue);
            Assert.Equal(EmailEvidenceSource.Body, value.Source);
        }

        [Fact]
        public void DoesNotPromoteArbitraryUnlabeledNumberToClaimNumber()
        {
            var evidence = Extract("סכום נזק 123456 ותאריך 30/08/2026", null);

            Assert.Empty(evidence.ClaimNumbers);
        }

        [Theory]
        [InlineData("סלולרי נהג: 050-123-4567", "0501234567")]
        [InlineData("Driver: phone +972 (50) 123-4567", "+972501234567")]
        public void ExtractsOnlyExplicitDriverPhoneLabels(string input, string expected)
        {
            var value = Assert.Single(Extract(null, input).DriverPhones);

            Assert.Equal(expected, value.NormalizedValue);
            Assert.Equal(EmailEvidenceExtractionKind.ExplicitLabel, value.ExtractionKind);
        }

        [Fact]
        public void ClientHintsRemainEmptyUntilTextContractIsVerified()
        {
            var evidence = Extract("לקוח: 12345", "שם לקוח: חברה לדוגמה");

            Assert.Empty(evidence.ClientHints);
        }

        [Fact]
        public void PolicyNumberIsNotPartOfCanonicalModelOrExtraction()
        {
            Assert.DoesNotContain(
                typeof(EmailCaseEvidence).GetProperties(),
                property => property.Name.Contains("Policy", StringComparison.OrdinalIgnoreCase));

            var evidence = Extract("מספר פוליסה: 12345678", null);
            Assert.Empty(evidence.ClaimNumbers);
            Assert.Empty(evidence.CourtCaseNumbers);
        }

        private EmailCaseEvidence Extract(string? subject, string? body)
            => _extractor.Extract(subject, body, maximumCandidates: 100);

        private static void AssertEvidence(
            EmailEvidenceValue actual,
            EmailEvidenceType type,
            string value,
            EmailEvidenceSource source,
            EmailEvidenceExtractionKind extractionKind)
        {
            Assert.Equal(type, actual.EvidenceType);
            Assert.Equal(value, actual.NormalizedValue);
            Assert.Equal(source, actual.Source);
            Assert.Equal(extractionKind, actual.ExtractionKind);
        }
    }
}
