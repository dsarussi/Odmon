using Microsoft.Extensions.Options;
using Odmon.Worker.Configuration;
using Odmon.Worker.OdcanitAccess;

namespace Odmon.Worker.Services
{
    public sealed record EmailFilingDocumentDestination(int DocCounter, string DestPath);

    public interface IEmailFilingDocumentWriter
    {
        Task<long> PreflightAsync(string msgFilePath, CancellationToken cancellationToken);

        Task<EmailFilingDocumentDestination> CreateDocumentRowAsync(
            int tikCounter,
            string subject,
            string msgFilePath,
            DateTime emailDateUtc,
            CancellationToken cancellationToken);

        Task<string> ResolveDestinationPathAsync(
            int docCounter,
            CancellationToken cancellationToken);

        Task<bool> IsVerifiedDestinationAsync(
            string destinationPath,
            long expectedLength,
            CancellationToken cancellationToken);

        Task CopyAndVerifyAsync(
            string msgFilePath,
            string destinationPath,
            long expectedLength,
            bool overwriteExisting,
            CancellationToken cancellationToken);
    }

    /// <summary>
    /// Reuses the existing Odcanit document-row contract and its application-side
    /// copy/size-verification convention. Row creation and physical copy are split
    /// so IntegrationDb can durably retain DocCounter and resume the same Odcanit
    /// row through a protected-path lookup after a process failure.
    /// </summary>
    public sealed class EmailFilingDocumentWriter(
        OdcanitDocumentWriter documentWriter,
        IOptions<EmailFilingSettings> options,
        ILogger<EmailFilingDocumentWriter> logger)
        : IEmailFilingDocumentWriter
    {
        private static readonly byte[] CompoundFileSignature =
            [0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1];
        private readonly EmailFilingSettings _settings = options.Value;

        public async Task<long> PreflightAsync(
            string msgFilePath,
            CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(msgFilePath) ||
                !Path.IsPathFullyQualified(msgFilePath) ||
                !string.Equals(Path.GetExtension(msgFilePath), ".msg", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("Email filing source must be an absolute .msg path.");
            }

            var sourceInfo = new FileInfo(msgFilePath);
            if (!sourceInfo.Exists || sourceInfo.Length < CompoundFileSignature.Length)
            {
                throw new InvalidDataException("Email filing source MSG is missing or empty.");
            }

            var signature = new byte[CompoundFileSignature.Length];
            await using var source = new FileStream(
                msgFilePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                81920,
                useAsync: true);
            var bytesRead = await source.ReadAsync(signature, cancellationToken);
            if (bytesRead != signature.Length || !signature.SequenceEqual(CompoundFileSignature))
            {
                throw new InvalidDataException("Email filing source is not a genuine Outlook MSG compound file.");
            }

            return sourceInfo.Length;
        }

        public async Task<EmailFilingDocumentDestination> CreateDocumentRowAsync(
            int tikCounter,
            string subject,
            string msgFilePath,
            DateTime emailDateUtc,
            CancellationToken cancellationToken)
        {
            if (tikCounter <= 0)
                throw new ArgumentOutOfRangeException(nameof(tikCounter));

            if (emailDateUtc < System.Data.SqlTypes.SqlDateTime.MinValue.Value ||
                emailDateUtc > System.Data.SqlTypes.SqlDateTime.MaxValue.Value)
            {
                throw new ArgumentOutOfRangeException(nameof(emailDateUtc));
            }

            // Repeat source validation immediately before the irreversible SP call.
            await PreflightAsync(msgFilePath, cancellationToken);
            var documentName = BuildDocumentName(subject);
            var result = await documentWriter.CreateDocumentRowAsync(
                tikCounter,
                documentName,
                msgFilePath,
                emailDateUtc,
                cancellationToken);

            return new EmailFilingDocumentDestination(result.DocCounter, result.DestPath);
        }

        public Task<string> ResolveDestinationPathAsync(
            int docCounter,
            CancellationToken cancellationToken)
            => documentWriter.ResolveMsgDestinationPathAsync(docCounter, cancellationToken);

        public async Task<bool> IsVerifiedDestinationAsync(
            string destinationPath,
            long expectedLength,
            CancellationToken cancellationToken)
        {
            destinationPath = ValidateDestinationPath(destinationPath);
            if (expectedLength < CompoundFileSignature.Length || !File.Exists(destinationPath))
                return false;

            var destinationInfo = new FileInfo(destinationPath);
            if (destinationInfo.Length != expectedLength)
                return false;

            var signature = new byte[CompoundFileSignature.Length];
            await using var destination = new FileStream(
                destinationPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                81920,
                useAsync: true);
            var bytesRead = await destination.ReadAsync(signature, cancellationToken);
            return bytesRead == signature.Length && signature.SequenceEqual(CompoundFileSignature);
        }

        public async Task CopyAndVerifyAsync(
            string msgFilePath,
            string destinationPath,
            long expectedLength,
            bool overwriteExisting,
            CancellationToken cancellationToken)
        {
            var actualLength = await PreflightAsync(msgFilePath, cancellationToken);
            if (actualLength != expectedLength)
                throw new IOException("Email MSG source changed after preflight.");

            destinationPath = ValidateDestinationPath(destinationPath);
            if (string.Equals(
                    Path.GetFullPath(msgFilePath),
                    Path.GetFullPath(destinationPath),
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new IOException("Email MSG source and Odcanit destination paths are identical.");
            }

            var destinationWriteStarted = false;
            try
            {
                var destinationDirectory = Path.GetDirectoryName(destinationPath);
                if (!string.IsNullOrWhiteSpace(destinationDirectory) &&
                    !Directory.Exists(destinationDirectory))
                {
                    Directory.CreateDirectory(destinationDirectory);
                }

                if (!overwriteExisting && File.Exists(destinationPath))
                {
                    throw new IOException(
                        "Odcanit MSG destination unexpectedly exists before the first copy attempt.");
                }

                await using (var source = new FileStream(
                                 msgFilePath,
                                 FileMode.Open,
                                 FileAccess.Read,
                                 FileShare.Read,
                                 81920,
                                 useAsync: true))
                await using (var destination = new FileStream(
                                 destinationPath,
                                 FileMode.Create,
                                 FileAccess.Write,
                                 FileShare.None,
                                 81920,
                                 useAsync: true))
                {
                    destinationWriteStarted = true;
                    await source.CopyToAsync(destination, cancellationToken);
                }

                if (!await IsVerifiedDestinationAsync(destinationPath, expectedLength, cancellationToken))
                {
                    TryDeletePartialDestination(destinationPath);
                    throw new IOException(
                        "Email MSG destination verification failed after the Odcanit document row was created.");
                }

                logger.LogInformation("EMAILFILING document copy verified. Size={Size}", expectedLength);
            }
            catch
            {
                if (destinationWriteStarted)
                    TryDeletePartialDestination(destinationPath);
                throw;
            }
        }

        internal static string BuildDocumentName(string? subject)
        {
            var sanitized = DocumentIngestionService.SanitizeFilename(
                subject ?? string.Empty,
                maxLength: 180).Trim().TrimEnd('.');
            return string.IsNullOrWhiteSpace(sanitized) ? "Email" : sanitized;
        }

        private string ValidateDestinationPath(string destinationPath)
        {
            if (string.IsNullOrWhiteSpace(destinationPath) ||
                !Path.IsPathFullyQualified(destinationPath) ||
                !string.Equals(Path.GetExtension(destinationPath), ".msg", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("Odcanit returned an invalid MSG destination path.");
            }

            if (!_settings.IsDestinationRootConfigurationValid() ||
                !IsAllowedDestinationPath(destinationPath, _settings.AllowedDestinationRoots))
            {
                throw new InvalidDataException(
                    "Odcanit returned an MSG destination outside the configured protected roots.");
            }

            return Path.GetFullPath(destinationPath);
        }

        internal static bool IsAllowedDestinationPath(
            string destinationPath,
            IEnumerable<string> allowedRoots)
            => string.Equals(
                   Path.GetExtension(destinationPath),
                   ".msg",
                   StringComparison.OrdinalIgnoreCase) &&
               SqlNetCourtDocumentFileResolver.IsPathUnderAllowedRoot(
                   destinationPath,
                   allowedRoots);

        private static void TryDeletePartialDestination(string path)
        {
            try
            {
                if (File.Exists(path))
                    File.Delete(path);
            }
            catch
            {
                // Best effort. The original write/verification failure remains primary.
            }
        }
    }
}
