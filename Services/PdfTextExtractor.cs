using System.Text;
using UglyToad.PdfPig;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;

namespace Odmon.Worker.Services
{
    public interface IPdfTextExtractor
    {
        Task<string> ExtractTextAsync(string filePath, CancellationToken ct);
    }

    public sealed class PdfTextExtractor : IPdfTextExtractor
    {
        public async Task<string> ExtractTextAsync(string filePath, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(filePath))
            {
                throw new ArgumentException("The PDF path is empty.", nameof(filePath));
            }

            if (!string.Equals(
                    System.IO.Path.GetExtension(filePath),
                    ".pdf",
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("The discovered document path is not a PDF file.");
            }

            await using var stream = new FileStream(
                filePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite,
                bufferSize: 64 * 1024,
                FileOptions.SequentialScan | FileOptions.Asynchronous);

            var header = new byte[4];
            var headerBytesRead = await stream.ReadAsync(header, ct);
            if (headerBytesRead != header.Length ||
                header[0] != 0x25 ||
                header[1] != 0x50 ||
                header[2] != 0x44 ||
                header[3] != 0x46)
            {
                throw new InvalidDataException("The discovered file does not start with PDF magic bytes.");
            }

            stream.Position = 0;
            using var document = PdfDocument.Open(stream);
            var text = new StringBuilder();
            foreach (var page in document.GetPages())
            {
                ct.ThrowIfCancellationRequested();
                if (text.Length > 0)
                {
                    text.AppendLine();
                }

                text.Append(ContentOrderTextExtractor.GetText(page));
            }

            var extracted = text.ToString();
            if (string.IsNullOrWhiteSpace(extracted))
            {
                throw new InvalidDataException("The PDF contains no usable text layer.");
            }

            return extracted;
        }
    }
}
