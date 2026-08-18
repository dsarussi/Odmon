using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Odmon.Worker.Data;
using Odmon.Worker.Monday;
using Odmon.Worker.Models;
using Odmon.Worker.OdcanitAccess;
using Odmon.Worker.Services;
using Xunit;

namespace Odmon.Worker.Tests
{
    public sealed class CaseIntakeCliTests
    {
        [Fact]
        public void TryParse_ExactForwardedServerArguments_DetectsReadOnlyCommand()
        {
            var arguments = new[] { "--case-intake-tik-counter", "40514" };

            var detected = CaseIntakeCli.TryParse(arguments, out var request);

            Assert.True(detected);
            Assert.Equal(40514, request.TikCounter);
            Assert.False(request.DumpPdfText);
            Assert.False(request.DumpNormalizedPdfText);
        }

        [Fact]
        public void TryParse_DumpPdfTextFlag_EnablesDiagnosticMode()
        {
            var arguments = new[]
            {
                "--case-intake-tik-counter", "40514",
                "--dump-pdf-text"
            };

            var detected = CaseIntakeCli.TryParse(arguments, out var request);

            Assert.True(detected);
            Assert.Equal(40514, request.TikCounter);
            Assert.True(request.DumpPdfText);
            Assert.False(request.DumpNormalizedPdfText);
        }

        [Fact]
        public void TryParse_DumpNormalizedPdfTextFlag_EnablesNormalizedDiagnosticMode()
        {
            var arguments = new[]
            {
                "--case-intake-tik-counter", "40514",
                "--dump-normalized-pdf-text"
            };

            var detected = CaseIntakeCli.TryParse(arguments, out var request);

            Assert.True(detected);
            Assert.Equal(40514, request.TikCounter);
            Assert.False(request.DumpPdfText);
            Assert.True(request.DumpNormalizedPdfText);
        }

        [Fact]
        public void TryParse_EqualsSyntax_DetectsReadOnlyCommand()
        {
            var detected = CaseIntakeCli.TryParse(
                ["--case-intake-tik-counter=40514"],
                out var request);

            Assert.True(detected);
            Assert.Equal(40514, request.TikCounter);
        }

        [Fact]
        public void TryParse_NoCaseIntakeOption_LeavesProductionDispatchUntouched()
        {
            var detected = CaseIntakeCli.TryParse(["--environment", "Production"], out _);

            Assert.False(detected);
        }

        public static TheoryData<string[]> InvalidArguments => new()
        {
            new[] { "--case-intake-tik-counter" },
            new[] { "--case-intake-tik-counter", "0" },
            new[] { "--case-intake-tik-counter", "-1" },
            new[] { "--case-intake-tik-counter", "not-a-number" },
            new[] { "--dump-pdf-text" },
            new[] { "--case-intake-tik-counter", "40514", "--dump-pdf-text=true" },
            new[] { "--dump-normalized-pdf-text" },
            new[] { "--case-intake-tik-counter", "40514", "--dump-normalized-pdf-text=true" },
            new[]
            {
                "--case-intake-tik-counter", "40514",
                "--dump-pdf-text",
                "--dump-normalized-pdf-text"
            }
        };

        [Theory]
        [MemberData(nameof(InvalidArguments))]
        public void TryParse_InvalidCaseIntakeOption_FailsClosed(string[] arguments)
        {
            Assert.Throws<ArgumentException>(() => CaseIntakeCli.TryParse(arguments, out _));
        }

        [Fact]
        public void TryParse_DuplicateCaseIntakeOption_FailsClosed()
        {
            var arguments = new[]
            {
                "--case-intake-tik-counter", "40514",
                "--case-intake-tik-counter=40514"
            };

            Assert.Throws<ArgumentException>(() => CaseIntakeCli.TryParse(arguments, out _));
        }

        [Fact]
        public void ReadOnlyHost_ContainsNoHostedServicesOrWriteIntegrations()
        {
            using var host = CaseIntakeCli.BuildReadOnlyHost(
                [
                    "--case-intake-tik-counter", "40514",
                    "--dump-normalized-pdf-text",
                    "--ConnectionStrings:OdcanitDb", "Server=localhost;Database=Odcanit;Integrated Security=true"
                ]);

            Assert.Empty(host.Services.GetServices<IHostedService>());
            Assert.Null(host.Services.GetService<IOdcanitWriter>());
            Assert.Null(host.Services.GetService<IMondayClient>());
            Assert.Null(host.Services.GetService<IntegrationDbContext>());

            using var scope = host.Services.CreateScope();
            Assert.NotNull(scope.ServiceProvider.GetService<CaseIntakeReadService>());
        }

        [Fact]
        public async Task DumpPdfText_PrintsDelimitedRawTextForApprovedDocumentsOnly()
        {
            var approved = new OdcanitCaseDocument(
                7001,
                CaseIntakeDocumentClassifier.PrivatePartyDemandLetterName,
                @"\\server\docs\demand.pdf",
                40514,
                "40514/1",
                "1",
                "PDF",
                new DateTime(2026, 8, 18));
            var unrelated = approved with
            {
                Id = 7002,
                Name = "unrelated_document",
                Path = @"\\server\docs\unrelated.pdf"
            };
            var reader = new FakeDocumentReader([approved, unrelated]);
            var extractor = new FakePdfTextExtractor("raw line 1\nraw line 2");
            using var output = new StringWriter();

            await CaseIntakeCli.DumpPdfTextAsync(
                reader,
                extractor,
                40514,
                output,
                CancellationToken.None);

            var printed = output.ToString();
            Assert.Contains("Document ID: 7001", printed, StringComparison.Ordinal);
            Assert.Contains(
                $"Document name: {CaseIntakeDocumentClassifier.PrivatePartyDemandLetterName}",
                printed,
                StringComparison.Ordinal);
            Assert.Contains(
                "BEGIN EXTRACTED TEXT\r\nraw line 1\nraw line 2\r\nEND EXTRACTED TEXT",
                printed,
                StringComparison.Ordinal);
            Assert.DoesNotContain("unrelated_document", printed, StringComparison.Ordinal);
            Assert.Equal([approved.Path], extractor.OpenedPaths);
        }

        [Fact]
        public async Task DumpNormalizedPdfText_PrintsProductionNormalizedTextWithDelimiters()
        {
            var approved = new OdcanitCaseDocument(
                2214486,
                CaseIntakeDocumentClassifier.PrivatePartyDemandLetterName,
                @"\\server\docs\demand.pdf",
                40514,
                "40514/1",
                "1",
                "PDF",
                new DateTime(2026, 8, 18));
            var reader = new FakeDocumentReader([approved]);
            var extractor = new FakePdfTextExtractor("תואמש ימד464.0₪");
            using var output = new StringWriter();

            await CaseIntakeCli.DumpNormalizedPdfTextAsync(
                reader,
                extractor,
                new HebrewPdfTextNormalizer(),
                40514,
                output,
                CancellationToken.None);

            var printed = output.ToString();
            Assert.Contains("Document ID: 2214486", printed, StringComparison.Ordinal);
            Assert.Contains(
                $"Document name: {CaseIntakeDocumentClassifier.PrivatePartyDemandLetterName}",
                printed,
                StringComparison.Ordinal);
            Assert.Contains(
                "BEGIN NORMALIZED TEXT\r\nדמי שמאות 464.0₪\r\nEND NORMALIZED TEXT",
                printed,
                StringComparison.Ordinal);
            Assert.DoesNotContain("תואמש ימד", printed, StringComparison.Ordinal);
            Assert.Equal([approved.Path], extractor.OpenedPaths);
        }

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
            private readonly string _text;

            public FakePdfTextExtractor(string text)
            {
                _text = text;
            }

            public List<string> OpenedPaths { get; } = [];

            public Task<string> ExtractTextAsync(string filePath, CancellationToken ct)
            {
                OpenedPaths.Add(filePath);
                return Task.FromResult(_text);
            }
        }
    }
}
