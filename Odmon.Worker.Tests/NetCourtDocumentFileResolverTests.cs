using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Odmon.Worker.Configuration;
using Odmon.Worker.OdcanitAccess;
using Xunit;

namespace Odmon.Worker.Tests
{
    public sealed class NetCourtDocumentFileResolverTests : IDisposable
    {
        private readonly string _testRoot =
            Path.Combine(Path.GetTempPath(), "odmon-netcourt-tests", Guid.NewGuid().ToString("N"));

        [Fact]
        public async Task NullOdDocId_ReturnsUnavailableWithoutDatabaseCall()
        {
            Directory.CreateDirectory(_testRoot);
            var resolver = CreateResolver();

            var result = await resolver.ResolveAsync(null, "2/2508", CancellationToken.None);

            Assert.False(result.IsAvailable);
            Assert.Contains("null", result.ErrorMessage!, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public async Task EmptyPath_ReturnsUnavailable()
        {
            Directory.CreateDirectory(_testRoot);
            var resolver = CreateResolver();

            var result = await resolver.ValidateResolvedPathAsync(
                string.Empty,
                2259074,
                "2/2508",
                CancellationToken.None);

            Assert.False(result.IsAvailable);
        }

        [Fact]
        public async Task PathOutsideAllowedRoots_ReturnsUnavailable()
        {
            Directory.CreateDirectory(_testRoot);
            var resolver = CreateResolver();
            var outside = Path.Combine(
                Path.GetTempPath(),
                "odmon-outside",
                Guid.NewGuid().ToString("N"),
                "decision.pdf");

            var result = await resolver.ValidateResolvedPathAsync(
                outside,
                2259074,
                "2/2508",
                CancellationToken.None);

            Assert.False(result.IsAvailable);
            Assert.Contains("outside", result.ErrorMessage!, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public async Task MissingFile_ReturnsUnavailable()
        {
            Directory.CreateDirectory(_testRoot);
            var resolver = CreateResolver();
            var path = Path.Combine(_testRoot, "missing.pdf");

            var result = await resolver.ValidateResolvedPathAsync(
                path,
                2259074,
                "2/2508",
                CancellationToken.None);

            Assert.False(result.IsAvailable);
            Assert.False(result.Exists);
        }

        [Fact]
        public async Task FileOverSizeLimit_ReturnsUnavailableWithoutOpeningContent()
        {
            Directory.CreateDirectory(_testRoot);
            var path = Path.Combine(_testRoot, "large.pdf");
            await File.WriteAllBytesAsync(path, "%PDF-oversized"u8.ToArray());
            var resolver = CreateResolver(maxAttachmentBytes: 4);

            var result = await resolver.ValidateResolvedPathAsync(
                path,
                2259074,
                "2/2508",
                CancellationToken.None);

            Assert.False(result.IsAvailable);
            Assert.True(result.Exists);
            Assert.Equal(new FileInfo(path).Length, result.FileSizeBytes);
        }

        [Fact]
        public async Task PdfExtensionWithInvalidMagic_ReturnsUnavailable()
        {
            Directory.CreateDirectory(_testRoot);
            var path = Path.Combine(_testRoot, "invalid.pdf");
            await File.WriteAllTextAsync(path, "not a pdf");
            var resolver = CreateResolver();

            var result = await resolver.ValidateResolvedPathAsync(
                path,
                2259074,
                "2/2508",
                CancellationToken.None);

            Assert.False(result.IsAvailable);
            Assert.Contains("magic", result.ErrorMessage!, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public async Task FileAccessIOException_ReturnsUnavailable()
        {
            Directory.CreateDirectory(_testRoot);
            var path = Path.Combine(_testRoot, "locked.pdf");
            await File.WriteAllBytesAsync(path, "%PDF-1.7"u8.ToArray());
            var resolver = CreateResolver();

            await using var exclusiveLock = new FileStream(
                path,
                FileMode.Open,
                FileAccess.ReadWrite,
                FileShare.None);
            var result = await resolver.ValidateResolvedPathAsync(
                path,
                2259074,
                "2/2508",
                CancellationToken.None);

            Assert.False(result.IsAvailable);
            Assert.Contains("access", result.ErrorMessage!, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public async Task ValidPdf_ReturnsAttachmentMetadata()
        {
            Directory.CreateDirectory(_testRoot);
            var path = Path.Combine(_testRoot, "decision.pdf");
            await File.WriteAllBytesAsync(path, "%PDF-1.7 test"u8.ToArray());
            var resolver = CreateResolver();

            var result = await resolver.ValidateResolvedPathAsync(
                path,
                2259074,
                "2/2508",
                CancellationToken.None);

            Assert.True(result.IsAvailable);
            Assert.Equal(path, result.FilePath);
            Assert.Equal("decision.pdf", result.FileName);
            Assert.Equal(new FileInfo(path).Length, result.FileSizeBytes);
        }

        [Fact]
        public void SimilarPrefixOutsideRoot_IsRejected()
        {
            var allowedRoot = Path.Combine(_testRoot, "Docs");
            var candidate = Path.Combine(_testRoot, "Docs-Other", "decision.pdf");

            Assert.False(SqlNetCourtDocumentFileResolver.IsPathUnderAllowedRoot(
                candidate,
                new[] { allowedRoot }));
        }

        [Fact]
        public void StoredProcedureDocumentExtension_IncludesLeadingDot()
        {
            Assert.Equal(".pdf", SqlNetCourtDocumentFileResolver.PdfDocumentExtension);
        }

        [Fact]
        public void StoredProcedureCommand_UsesVerifiedPositionalArgumentOrder()
        {
            Assert.Equal(
                "EXEC dbo.procDocumentsGroup_BuildDocPath @docCounterValue, @docExtensionValue, @protectedDocPathValue OUTPUT;",
                SqlNetCourtDocumentFileResolver.BuildDocPathCommandText);
        }

        private SqlNetCourtDocumentFileResolver CreateResolver(long maxAttachmentBytes = 10485760)
        {
            var dbOptions = new DbContextOptionsBuilder<OdcanitDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
                .Options;
            var settings = new NetCourtDecisionAlertSettings
            {
                MaxAttachmentBytes = maxAttachmentBytes,
                AttachmentAllowedRoots = new[] { _testRoot }
            };

            return new SqlNetCourtDocumentFileResolver(
                new OdcanitDbContext(dbOptions),
                Options.Create(settings),
                NullLogger<SqlNetCourtDocumentFileResolver>.Instance);
        }

        public void Dispose()
        {
            if (Directory.Exists(_testRoot))
            {
                Directory.Delete(_testRoot, recursive: true);
            }
        }
    }
}
