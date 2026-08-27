using MimeKit;
using Microsoft.Extensions.Options;
using Odmon.Worker.Configuration;
using MsgKit.Enums;
using MsgEmail = MsgKit.Email;
using MsgSender = MsgKit.Sender;

namespace Odmon.Worker.Services
{
    internal sealed record NeutralEmailAddress(string Address, string DisplayName);

    internal sealed record NeutralEmailAttachment(
        string FileName,
        byte[] Data,
        bool IsInline,
        string? ContentId,
        string? MimeType,
        int RenderingPosition = -1);

    internal sealed record NeutralEmail(
        NeutralEmailAddress Sender,
        IReadOnlyList<NeutralEmailAddress> To,
        IReadOnlyList<NeutralEmailAddress> Cc,
        IReadOnlyList<NeutralEmailAddress> Bcc,
        string Subject,
        DateTime? SentOnUtc,
        string? BodyText,
        string? BodyHtml,
        IReadOnlyList<NeutralEmailAttachment> Attachments,
        string? InternetMessageId,
        string? ConversationIndex,
        string? ConversationTopic,
        string? TransportHeaders);

    internal static class NeutralEmailMimeReader
    {
        public static async Task<NeutralEmail> ReadAsync(
            byte[] mimeBytes,
            int maximumAttachmentCount,
            CancellationToken cancellationToken)
        {
            await using var stream = new MemoryStream(mimeBytes, writable: false);
            using var message = await MimeMessage.LoadAsync(stream, cancellationToken);
            return FromMime(message, maximumAttachmentCount);
        }

        internal static NeutralEmail FromMime(MimeMessage message, int maximumAttachmentCount)
        {
            var sender = message.Sender ?? message.From.Mailboxes.FirstOrDefault()
                ?? throw new InvalidDataException("The MIME message has no SMTP sender.");
            var attachments = new List<NeutralEmailAttachment>();
            var attachmentNumber = 0;
            foreach (var part in message.BodyParts.OfType<MimePart>())
            {
                if (part.Content == null || !ShouldCopyPart(part, message.HtmlBody))
                {
                    continue;
                }

                attachmentNumber++;
                if (attachmentNumber > maximumAttachmentCount)
                {
                    throw new InvalidDataException(
                        "Email MIME exceeds the configured attachment-count limit.");
                }
                using var data = new MemoryStream();
                part.Content.DecodeTo(data);
                var contentId = NormalizeContentId(part.ContentId);
                var inline = IsInline(part) ||
                             (!string.IsNullOrWhiteSpace(contentId) &&
                              (message.HtmlBody?.Contains(
                                  "cid:" + contentId,
                                  StringComparison.OrdinalIgnoreCase) ?? false));
                attachments.Add(new NeutralEmailAttachment(
                    GetFileName(part, attachmentNumber),
                    data.ToArray(),
                    inline,
                    string.IsNullOrWhiteSpace(contentId) ? null : contentId,
                    part.ContentType.MimeType));
            }

            return new NeutralEmail(
                new NeutralEmailAddress(sender.Address, sender.Name ?? string.Empty),
                ConvertAddresses(message.To),
                ConvertAddresses(message.Cc),
                ConvertAddresses(message.Bcc),
                message.Subject ?? string.Empty,
                message.Date == DateTimeOffset.MinValue ? null : message.Date.UtcDateTime,
                message.TextBody,
                message.HtmlBody,
                attachments,
                message.MessageId,
                GetHeader(message, "Thread-Index"),
                GetHeader(message, "Thread-Topic"),
                BuildHeaders(message));
        }

        private static IReadOnlyList<NeutralEmailAddress> ConvertAddresses(
            InternetAddressList addresses)
            => addresses.Mailboxes
                .Select(address => new NeutralEmailAddress(
                    address.Address,
                    address.Name ?? string.Empty))
                .ToArray();

        private static bool ShouldCopyPart(MimePart part, string? htmlBody)
        {
            if (part is not TextPart)
            {
                return true;
            }

            var contentId = NormalizeContentId(part.ContentId);
            return part.IsAttachment ||
                   IsInline(part) ||
                   (!string.IsNullOrWhiteSpace(contentId) &&
                    (htmlBody?.Contains(
                        "cid:" + contentId,
                        StringComparison.OrdinalIgnoreCase) ?? false));
        }

        private static bool IsInline(MimePart part)
            => string.Equals(
                part.ContentDisposition?.Disposition,
                ContentDisposition.Inline,
                StringComparison.OrdinalIgnoreCase);

        private static string GetFileName(MimePart part, int index)
        {
            if (!string.IsNullOrWhiteSpace(part.FileName))
            {
                var leafName = Path.GetFileName(part.FileName);
                var invalidCharacters = Path.GetInvalidFileNameChars();
                var sanitized = new string(leafName
                    .Where(character => !invalidCharacters.Contains(character))
                    .Take(180)
                    .ToArray())
                    .Trim()
                    .TrimEnd('.');
                if (!string.IsNullOrWhiteSpace(sanitized))
                    return sanitized;
            }

            var extension = part.ContentType.MimeType.ToLowerInvariant() switch
            {
                "image/png" => ".png",
                "image/jpeg" => ".jpg",
                "image/gif" => ".gif",
                "application/pdf" => ".pdf",
                _ => ".bin"
            };
            return $"attachment-{index}{extension}";
        }

        private static string BuildHeaders(MimeMessage message)
            => string.Join(
                   "\r\n",
                   message.Headers.Select(header =>
                       $"{header.Field}: {header.Value.Replace('\r', ' ').Replace('\n', ' ')}")) +
               "\r\n";

        private static string? GetHeader(MimeMessage message, string field)
            => message.Headers.FirstOrDefault(header =>
                string.Equals(header.Field, field, StringComparison.OrdinalIgnoreCase))?.Value;

        internal static string NormalizeContentId(string? value)
            => (value ?? string.Empty).Trim().Trim('<', '>');
    }

    internal static class OutlookMsgWriter
    {
        private static readonly byte[] CompoundFileSignature =
            [0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1];

        public static byte[] Generate(NeutralEmail source)
        {
            using var email = new MsgEmail(
                new MsgSender(source.Sender.Address, source.Sender.DisplayName),
                source.Subject,
                draft: false,
                readReceipt: false);
            foreach (var recipient in source.To)
                email.Recipients.AddTo(recipient.Address, recipient.DisplayName);
            foreach (var recipient in source.Cc)
                email.Recipients.AddCc(recipient.Address, recipient.DisplayName);
            foreach (var recipient in source.Bcc)
                email.Recipients.AddBcc(recipient.Address, recipient.DisplayName);

            email.Subject = source.Subject;
            email.BodyText = source.BodyText ?? string.Empty;
            email.BodyHtml = source.BodyHtml ?? string.Empty;
            email.SentOn = source.SentOnUtc;
            email.InternetMessageId = source.InternetMessageId ?? string.Empty;
            email.TransportMessageHeadersText = source.TransportHeaders ?? string.Empty;
            if (TryDecodeConversationIndex(source.ConversationIndex, out var conversationIndex))
            {
                email.AddProperty(
                    MsgKit.PropertyTags.PR_CONVERSATION_INDEX,
                    conversationIndex,
                    PropertyFlags.PROPATTR_READABLE | PropertyFlags.PROPATTR_WRITABLE);
            }

            if (!string.IsNullOrWhiteSpace(source.ConversationTopic))
            {
                email.AddProperty(
                    MsgKit.PropertyTags.PR_CONVERSATION_TOPIC_W,
                    source.ConversationTopic,
                    PropertyFlags.PROPATTR_READABLE | PropertyFlags.PROPATTR_WRITABLE);
            }

            foreach (var attachment in source.Attachments)
            {
                var stream = new MemoryStream(attachment.Data, writable: false);
                email.Attachments.Add(
                    stream,
                    attachment.FileName,
                    attachment.RenderingPosition,
                    attachment.IsInline,
                    attachment.IsInline ? attachment.ContentId ?? string.Empty : string.Empty);
            }

            using var output = new MemoryStream();
            email.Save(output);
            var bytes = output.ToArray();
            if (bytes.Length < CompoundFileSignature.Length ||
                !bytes.AsSpan(0, CompoundFileSignature.Length).SequenceEqual(CompoundFileSignature))
            {
                throw new InvalidDataException("MsgKit output is not an Outlook MSG compound file.");
            }

            return bytes;
        }

        private static bool TryDecodeConversationIndex(string? value, out byte[] bytes)
        {
            bytes = [];
            if (string.IsNullOrWhiteSpace(value))
                return false;

            var normalized = value.Trim();
            try
            {
                bytes = normalized.Length % 2 == 0 &&
                        normalized.All(Uri.IsHexDigit)
                    ? Convert.FromHexString(normalized)
                    : Convert.FromBase64String(normalized);
                return bytes.Length > 0;
            }
            catch (FormatException)
            {
                bytes = [];
                return false;
            }
        }
    }

    public interface IEmailMsgArtifact : IAsyncDisposable
    {
        string FilePath { get; }
    }

    public interface IEmailMsgGenerator
    {
        Task<IEmailMsgArtifact> GenerateAsync(
            byte[] mimeBytes,
            CancellationToken cancellationToken);
    }

    public sealed class EmailMsgGenerator(IOptions<EmailFilingSettings> options) : IEmailMsgGenerator
    {
        private const string TempDirectoryName = "odmon-email-filing";
        private readonly EmailFilingSettings _settings = options.Value;

        public async Task<IEmailMsgArtifact> GenerateAsync(
            byte[] mimeBytes,
            CancellationToken cancellationToken)
        {
            if (_settings.MaxMimeMessageBytes <= 0 ||
                mimeBytes.Length == 0 ||
                mimeBytes.LongLength > _settings.MaxMimeMessageBytes)
            {
                throw new InvalidDataException(
                    "Email MIME is empty or exceeds the configured size limit.");
            }

            if (_settings.MaxMimeAttachmentCount <= 0)
                throw new InvalidOperationException("Email MIME attachment-count limit must be positive.");

            var neutral = await NeutralEmailMimeReader.ReadAsync(
                mimeBytes,
                _settings.MaxMimeAttachmentCount,
                cancellationToken);
            var bytes = OutlookMsgWriter.Generate(neutral);
            if (bytes.LongLength > _settings.MaxMimeMessageBytes)
                throw new InvalidDataException("Generated MSG exceeds the configured size limit.");
            var directory = Path.Combine(Path.GetTempPath(), TempDirectoryName);
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, $"{Guid.NewGuid():N}.msg");
            try
            {
                await File.WriteAllBytesAsync(path, bytes, cancellationToken);
                return new TemporaryEmailMsgArtifact(path);
            }
            catch
            {
                TryDelete(path);
                throw;
            }
        }

        private sealed class TemporaryEmailMsgArtifact(string filePath) : IEmailMsgArtifact
        {
            public string FilePath { get; } = filePath;

            public ValueTask DisposeAsync()
            {
                TryDelete(FilePath);
                return ValueTask.CompletedTask;
            }
        }

        private static void TryDelete(string path)
        {
            try
            {
                if (File.Exists(path))
                    File.Delete(path);
            }
            catch
            {
                // Best effort: never mask the filing result with cleanup failure.
            }
        }
    }
}
