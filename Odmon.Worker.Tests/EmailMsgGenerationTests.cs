using System.Text;
using MimeKit;
using Odmon.Worker.Services;
using Xunit;

namespace Odmon.Worker.Tests
{
    public sealed class EmailMsgGenerationTests
    {
        [Fact]
        public async Task MimeBoundaryPreservesAttachmentsInlineCidAndHebrew()
        {
            var mimeBytes = CreateSyntheticMimeBytes();
            var neutral = await NeutralEmailMimeReader.ReadAsync(mimeBytes, CancellationToken.None);

            Assert.Contains("בדיקה", neutral.Subject);
            Assert.Contains("שלום", neutral.BodyText);
            Assert.Contains("שלום", neutral.BodyHtml);
            var pdf = Assert.Single(neutral.Attachments.Where(value => value.FileName == "synthetic.pdf"));
            Assert.False(pdf.IsInline);
            Assert.True(pdf.Data.Length > 0);
            var inline = Assert.Single(neutral.Attachments.Where(value => value.FileName == "inline.png"));
            Assert.True(inline.IsInline);
            Assert.Equal("inline-synthetic@odmon.invalid", inline.ContentId);

            var msg = OutlookMsgWriter.Generate(neutral);
            Assert.Equal(
                new byte[] { 0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1 },
                msg[..8]);
        }

        [Fact]
        public async Task TemporaryMsgIsRandomAndDeletedOnDispose()
        {
            var generator = new EmailMsgGenerator();
            var artifact = await generator.GenerateAsync(
                CreateSyntheticMimeBytes(),
                CancellationToken.None);
            var path = artifact.FilePath;

            Assert.True(File.Exists(path));
            Assert.Equal(".msg", Path.GetExtension(path));
            Assert.DoesNotContain("synthetic", Path.GetFileName(path), StringComparison.OrdinalIgnoreCase);

            await artifact.DisposeAsync();

            Assert.False(File.Exists(path));
        }

        [Fact]
        public async Task GenerationExceptionLeavesNoTemporaryMsg()
        {
            var directory = Path.Combine(Path.GetTempPath(), "odmon-email-filing");
            var before = Directory.Exists(directory)
                ? Directory.GetFiles(directory, "*.msg").ToHashSet(StringComparer.OrdinalIgnoreCase)
                : [];
            var generator = new EmailMsgGenerator();

            await Assert.ThrowsAnyAsync<Exception>(() => generator.GenerateAsync(
                Encoding.UTF8.GetBytes("not a MIME message"),
                CancellationToken.None));

            var after = Directory.Exists(directory)
                ? Directory.GetFiles(directory, "*.msg").ToHashSet(StringComparer.OrdinalIgnoreCase)
                : [];
            Assert.True(before.SetEquals(after));
        }

        private static byte[] CreateSyntheticMimeBytes()
        {
            using var message = new MimeMessage
            {
                Subject = "ODMON 9/1984 – בדיקה English",
                MessageId = "<synthetic@odmon.invalid>",
                Date = new DateTimeOffset(2026, 8, 27, 10, 30, 0, TimeSpan.Zero)
            };
            message.From.Add(new MailboxAddress("שולח", "sender@odmon.invalid"));
            message.To.Add(new MailboxAddress("נמען", "recipient@odmon.invalid"));
            message.Cc.Add(new MailboxAddress("Copy", "copy@odmon.invalid"));
            var builder = new BodyBuilder
            {
                TextBody = "שלום ODMON\r\nEnglish plain text.",
                HtmlBody = "<html><body><p>שלום <b>ODMON</b></p><img src=\"cid:inline-synthetic@odmon.invalid\"></body></html>"
            };
            var inline = builder.LinkedResources.Add(
                "inline.png",
                new MemoryStream(Convert.FromBase64String(
                    "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=")));
            inline.ContentId = "inline-synthetic@odmon.invalid";
            builder.Attachments.Add(
                "synthetic.pdf",
                new MemoryStream(Encoding.ASCII.GetBytes(
                    "%PDF-1.4\n1 0 obj<</Type/Catalog>>endobj\ntrailer<</Root 1 0 R>>\n%%EOF")),
                ContentType.Parse("application/pdf"));
            message.Body = builder.ToMessageBody();
            using var output = new MemoryStream();
            message.WriteTo(output);
            return output.ToArray();
        }
    }
}
