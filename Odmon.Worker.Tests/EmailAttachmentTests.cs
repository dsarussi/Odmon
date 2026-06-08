using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Odmon.Worker.Services;
using Xunit;

namespace Odmon.Worker.Tests
{
    public sealed class EmailAttachmentTests : IDisposable
    {
        private readonly string _testRoot =
            Path.Combine(Path.GetTempPath(), "odmon-email-tests", Guid.NewGuid().ToString("N"));

        [Fact]
        public async Task QueueEmail_DirectMessageCarriesAttachment()
        {
            Directory.CreateDirectory(_testRoot);
            var path = Path.Combine(_testRoot, "decision.pdf");
            await File.WriteAllBytesAsync(path, "%PDF-test"u8.ToArray());
            using var notifier = CreateNotifier();
            var descriptor = new EmailAttachmentDescriptor(
                path,
                "decision.pdf",
                "application/pdf");

            Assert.True(notifier.QueueEmail(
                "subject",
                "body",
                new[] { "employee@example.com" },
                attachments: new[] { descriptor }));

            var message = await notifier.Reader.ReadAsync();
            Assert.Equal(EmailMessageType.Direct, message.Type);
            Assert.Equal(new[] { descriptor }, message.Attachments);
        }

        [Fact]
        public async Task QueueEmail_DirectMessageCarriesBccWithoutReplacingTo()
        {
            using var notifier = CreateNotifier();

            Assert.True(notifier.QueueEmail(
                "subject",
                "body",
                new[] { "employee@example.com" },
                bccRecipients: new[] { "monitor@example.com" }));

            var message = await notifier.Reader.ReadAsync();
            Assert.Equal(new[] { "employee@example.com" }, message.Recipients);
            Assert.Equal(new[] { "monitor@example.com" }, message.BccRecipients);

            using var mail = EmailNotifier.CreateMailMessage(
                message,
                "odmon@example.com",
                message.Recipients!,
                includeAttachments: true);
            Assert.Equal("employee@example.com", Assert.Single(mail.To).Address);
            Assert.Equal("monitor@example.com", Assert.Single(mail.Bcc).Address);
        }

        [Fact]
        public async Task MailMessage_DisposeReleasesAttachmentStream()
        {
            Directory.CreateDirectory(_testRoot);
            var path = Path.Combine(_testRoot, "decision.pdf");
            await File.WriteAllBytesAsync(path, "%PDF-test"u8.ToArray());
            var message = new EmailMessage
            {
                Subject = "subject",
                Body = "body",
                Type = EmailMessageType.Direct,
                Recipients = new[] { "employee@example.com" },
                BccRecipients = new[] { "monitor@example.com" },
                Attachments = new[]
                {
                    new EmailAttachmentDescriptor(path, "decision.pdf", "application/pdf")
                }
            };

            using (var mail = EmailNotifier.CreateMailMessage(
                       message,
                       "odmon@example.com",
                       message.Recipients,
                       includeAttachments: true))
            {
                Assert.Single(mail.Attachments);
                Assert.Equal("monitor@example.com", Assert.Single(mail.Bcc).Address);
                Assert.Equal("decision.pdf", mail.Attachments[0].Name);
                Assert.Equal("application/pdf", mail.Attachments[0].ContentType.MediaType);
            }

            using var exclusive = new FileStream(
                path,
                FileMode.Open,
                FileAccess.ReadWrite,
                FileShare.None);
            Assert.True(exclusive.CanRead);
        }

        [Fact]
        public async Task CriticalAlert_RemainsAttachmentFreeAndUsesGlobalRecipients()
        {
            using var notifier = CreateNotifier();

            notifier.QueueCriticalAlert("critical", "body");

            var message = await notifier.Reader.ReadAsync();
            Assert.Equal(EmailMessageType.Critical, message.Type);
            Assert.Null(message.Recipients);
            Assert.Null(message.BccRecipients);
            Assert.Null(message.Attachments);
        }

        [Fact]
        public async Task DailySummary_DoesNotInheritNetCourtBcc()
        {
            using var notifier = CreateNotifier();

            await notifier.SendDailySummaryAsync("summary", "<p>body</p>", CancellationToken.None);

            var message = await notifier.Reader.ReadAsync();
            Assert.Equal(EmailMessageType.DailySummary, message.Type);
            Assert.Null(message.BccRecipients);
        }

        private static EmailNotifier CreateNotifier()
        {
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Email:Enabled"] = "true",
                    ["Email:MaxEmailsPerHour"] = "100",
                    ["Email:Recipients:0"] = "global@example.com",
                    ["NetCourtDecisionAlerts:BccRecipients:0"] = "monitor@example.com"
                })
                .Build();

            return new EmailNotifier(
                NullLogger<EmailNotifier>.Instance,
                configuration);
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
