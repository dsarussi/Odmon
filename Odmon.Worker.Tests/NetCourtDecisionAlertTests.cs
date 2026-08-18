using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Odmon.Worker.Configuration;
using Odmon.Worker.Data;
using Odmon.Worker.Models;
using Odmon.Worker.OdcanitAccess;
using Odmon.Worker.Services;
using Xunit;

namespace Odmon.Worker.Tests
{
    public class NetCourtDecisionAlertTests
    {
        [Fact]
        public async Task DocDateBeforeStartDate_IsIgnored()
        {
            await using var db = CreateDb();
            var reader = new FakeDocumentReader
            {
                Documents =
                {
                    Decision(
                        counter: 1,
                        courtDocumentId: 101,
                        docDate: new DateTime(2026, 6, 6))
                }
            };
            var email = new FakeEmailNotifier();
            var service = CreateService(db, reader, email, "Test");

            var result = await service.RunAsync(CancellationToken.None);

            Assert.Equal(new DateTime(2026, 6, 7), reader.LastStartFromDocDate);
            Assert.Equal(0, result.CandidatesDetected);
            Assert.Empty(await db.NetCourtDecisionAlerts.ToListAsync());
            Assert.Empty(email.DirectMessages);
        }

        [Fact]
        public async Task DocDateOnOrAfterStartDate_IsProcessed()
        {
            await using var db = CreateDb();
            var reader = new FakeDocumentReader
            {
                Documents =
                {
                    Decision(
                        counter: 2,
                        courtDocumentId: 102,
                        docDate: new DateTime(2026, 6, 7)),
                    Decision(
                        counter: 3,
                        courtDocumentId: 103,
                        docDate: new DateTime(2026, 6, 8))
                }
            };
            var email = new FakeEmailNotifier();
            var service = CreateService(db, reader, email, "Test");

            var result = await service.RunAsync(CancellationToken.None);

            Assert.Equal(2, result.EmailsQueued);
            Assert.Equal(2, email.DirectMessages.Count);
            Assert.Equal(2, await db.NetCourtDecisionAlerts.CountAsync());
        }

        [Fact]
        public async Task TsCreateDateDoesNotAffectDetection()
        {
            await using var db = CreateDb();
            var reader = new FakeDocumentReader
            {
                Documents =
                {
                    Decision(
                        counter: 20,
                        courtDocumentId: 120,
                        createdAtUtc: new DateTime(2000, 1, 1),
                        docDate: new DateTime(2026, 6, 7))
                }
            };
            var email = new FakeEmailNotifier();
            var service = CreateService(db, reader, email, "Test");

            var result = await service.RunAsync(CancellationToken.None);

            Assert.Equal(1, result.CandidatesDetected);
            Assert.Single(email.DirectMessages);
        }

        [Fact]
        public async Task CounterOrderDoesNotAffectDetection()
        {
            await using var db = CreateDb();
            var reader = new FakeDocumentReader
            {
                Documents =
                {
                    Decision(
                        counter: 900,
                        courtDocumentId: 900,
                        docDate: new DateTime(2026, 6, 7)),
                    Decision(
                        counter: 100,
                        courtDocumentId: 100,
                        docDate: new DateTime(2026, 6, 8))
                }
            };
            var email = new FakeEmailNotifier();
            var service = CreateService(db, reader, email, "Test");

            var result = await service.RunAsync(CancellationToken.None);

            Assert.Equal(2, result.EmailsQueued);
            Assert.Equal(2, await db.NetCourtDecisionAlerts.CountAsync());
        }

        [Fact]
        public async Task TrackedRowsDoNotBlockLaterUnprocessedRows()
        {
            await using var db = CreateDb();
            db.NetCourtDecisionAlerts.Add(new NetCourtDecisionAlert
            {
                DocumentIdentity = "CourtDocumentID:101",
                TikCounter = 77,
                NetCourtCounter = 101,
                CourtDocumentID = 101,
                DocType = 2,
                DocDate = new DateTime(2026, 6, 7),
                EmailMode = "Test",
                Status = NetCourtDecisionAlertStatuses.TestEmailQueued,
                CreatedAtUtc = DateTime.UtcNow
            });
            await db.SaveChangesAsync();
            var reader = new FakeDocumentReader
            {
                Documents =
                {
                    Decision(
                        counter: 101,
                        courtDocumentId: 101,
                        docDate: new DateTime(2026, 6, 7)),
                    Decision(
                        counter: 102,
                        courtDocumentId: 102,
                        docDate: new DateTime(2026, 6, 8))
                }
            };
            var email = new FakeEmailNotifier();
            var service = CreateService(db, reader, email, "Test", maxBatchSize: 1);

            var result = await service.RunAsync(CancellationToken.None);

            Assert.Equal(1, result.AlreadyProcessed);
            Assert.Equal(1, result.EmailsQueued);
            Assert.Single(email.DirectMessages);
            Assert.Equal(2, await db.NetCourtDecisionAlerts.CountAsync());
        }

        [Fact]
        public async Task NewDecision_InTestMode_QueuesOnceOnlyToTestRecipient()
        {
            await using var db = CreateDb();
            var reader = new FakeDocumentReader
            {
                Documents = { Decision(counter: 3, courtDocumentId: 103, tikCounter: 77) }
            };
            var email = new FakeEmailNotifier();
            var service = CreateService(db, reader, email, "Test");

            await service.RunAsync(CancellationToken.None);
            await service.RunAsync(CancellationToken.None);

            var message = Assert.Single(email.DirectMessages);
            Assert.Equal(new[] { EmailAutomationService.AllowedTestRecipient }, message.Recipients);
            Assert.DoesNotContain(EmailAutomationService.AmirEmail, message.Recipients);
            Assert.DoesNotContain(EmailAutomationService.YonatanEmail, message.Recipients);
            Assert.Empty(message.BccRecipients);
            Assert.Equal("החלטה חדשה בתיק 99/91005", message.Subject);
            Assert.StartsWith("שלום ", message.Body);
            Assert.Contains($"מצב בדיקה - המייל המקורי היה מיועד אל: {EmailAutomationService.YonatanEmail}", message.Body);

            var tracked = await db.NetCourtDecisionAlerts.SingleAsync();
            Assert.Equal(EmailAutomationService.YonatanEmail, tracked.IntendedRecipientEmail);
            Assert.Equal(EmailAutomationService.AllowedTestRecipient, tracked.ActualRecipientEmail);
            Assert.Equal(NetCourtDecisionAlertStatuses.TestEmailQueued, tracked.Status);
        }

        [Fact]
        public async Task NewDecision_InLiveMode_QueuesToRoutedEmployee()
        {
            await using var db = CreateDb();
            var reader = new FakeDocumentReader
            {
                Documents = { Decision(counter: 4, courtDocumentId: 104, tikCounter: 88) }
            };
            var email = new FakeEmailNotifier();
            var service = CreateService(db, reader, email, "Live");

            await service.RunAsync(CancellationToken.None);

            var message = Assert.Single(email.DirectMessages);
            Assert.Equal(new[] { EmailAutomationService.AmirEmail }, message.Recipients);
            Assert.Empty(message.BccRecipients);
            Assert.StartsWith("שלום ", message.Body);
            Assert.Contains("התקבלה החלטה חדשה בתיק - 98/91006", message.Body);
            Assert.DoesNotContain("מצב בדיקה", message.Body);
            Assert.Equal(
                NetCourtDecisionAlertStatuses.LiveEmailQueued,
                (await db.NetCourtDecisionAlerts.SingleAsync()).Status);
        }

        [Fact]
        public async Task Client101_InLiveMode_RoutesToPrimaryEmployee()
        {
            await using var db = CreateDb();
            var service = CreateService(
                db,
                new FakeDocumentReader(),
                new FakeEmailNotifier(),
                "Live");

            var resolved = service.TryResolveRecipients(
                101,
                out var intendedRecipient,
                out var actualRecipients);

            Assert.True(resolved);
            Assert.Equal(EmailAutomationService.AmirEmail, intendedRecipient);
            Assert.Equal(new[] { EmailAutomationService.AmirEmail }, actualRecipients);
        }

        [Fact]
        public async Task MissingRouting_IsRecordedAndDoesNotQueueOrCrash()
        {
            await using var db = CreateDb();
            var reader = new FakeDocumentReader
            {
                Documents = { Decision(counter: 5, courtDocumentId: 105, tikCounter: 99) }
            };
            var resolver = new FakeCaseResolver
            {
                Cases =
                {
                    new OdcanitCase
                    {
                        TikCounter = 99,
                        TikNumber = "999/1",
                        ClientVisualID = "999\\1"
                    }
                }
            };
            var email = new FakeEmailNotifier();
            var service = CreateService(
                db,
                reader,
                email,
                "Live",
                resolver,
                bccRecipients: new[] { EmailAutomationService.AllowedTestRecipient });

            var result = await service.RunAsync(CancellationToken.None);

            Assert.Equal(1, result.MissingRouting);
            Assert.Empty(email.DirectMessages);
            Assert.Equal(
                NetCourtDecisionAlertStatuses.MissingRouting,
                (await db.NetCourtDecisionAlerts.SingleAsync()).Status);
        }

        [Fact]
        public async Task NullOdDocId_QueuesEmailWithoutAttachment()
        {
            await using var db = CreateDb();
            var reader = new FakeDocumentReader
            {
                Documents = { Decision(counter: 30, courtDocumentId: 130, odDocId: null) }
            };
            var email = new FakeEmailNotifier();
            var fileResolver = new FakeDocumentFileResolver
            {
                Result = NetCourtDocumentFileResult.Unavailable("ODDocID is null.")
            };
            var service = CreateService(
                db,
                reader,
                email,
                "Test",
                fileResolver: fileResolver,
                attachDecisionPdf: true);

            await service.RunAsync(CancellationToken.None);

            var message = Assert.Single(email.DirectMessages);
            Assert.Empty(message.Attachments);
            Assert.Contains("לא צורף קובץ ההחלטה: מזהה המסמך חסר", message.Body);
            Assert.Equal(new long?[] { null }, fileResolver.RequestedOdDocIds);
        }

        [Theory]
        [InlineData("Resolved path is empty.")]
        [InlineData("Resolved path is outside allowed roots.")]
        [InlineData("File does not exist.")]
        [InlineData("File exceeds the configured attachment size limit.")]
        [InlineData("File does not start with PDF magic bytes.")]
        [InlineData("Access denied.")]
        public async Task UnavailableAttachment_QueuesEmailWithoutAttachment(string reason)
        {
            await using var db = CreateDb();
            var reader = new FakeDocumentReader
            {
                Documents = { Decision(counter: 31, courtDocumentId: 131, odDocId: 9103001) }
            };
            var email = new FakeEmailNotifier();
            var fileResolver = new FakeDocumentFileResolver
            {
                Result = NetCourtDocumentFileResult.Unavailable(reason)
            };
            var service = CreateService(
                db,
                reader,
                email,
                "Test",
                fileResolver: fileResolver,
                attachDecisionPdf: true);

            await service.RunAsync(CancellationToken.None);

            var message = Assert.Single(email.DirectMessages);
            Assert.Empty(message.Attachments);
            Assert.Contains("לא צורף קובץ ההחלטה:", message.Body);
            if (reason.Contains("size", StringComparison.OrdinalIgnoreCase))
            {
                Assert.Contains("גדול מהמגבלה המותרת", message.Body);
            }
        }

        [Fact]
        public async Task FileAccessException_QueuesEmailWithoutAttachment()
        {
            await using var db = CreateDb();
            var reader = new FakeDocumentReader
            {
                Documents = { Decision(counter: 32, courtDocumentId: 132, odDocId: 9103001) }
            };
            var email = new FakeEmailNotifier();
            var fileResolver = new FakeDocumentFileResolver
            {
                ExceptionToThrow = new IOException("File temporarily unavailable.")
            };
            var service = CreateService(
                db,
                reader,
                email,
                "Test",
                fileResolver: fileResolver,
                attachDecisionPdf: true);

            await service.RunAsync(CancellationToken.None);

            var message = Assert.Single(email.DirectMessages);
            Assert.Empty(message.Attachments);
            Assert.Contains("לא צורף קובץ ההחלטה: לא ניתן היה לקרוא את קובץ ההחלטה", message.Body);
        }

        [Fact]
        public async Task ValidPdf_InTestMode_QueuesOnlyToTestRecipientWithAttachment()
        {
            await using var db = CreateDb();
            var reader = new FakeDocumentReader
            {
                Documents = { Decision(counter: 33, courtDocumentId: 133, odDocId: 9103001) }
            };
            var email = new FakeEmailNotifier();
            var fileResolver = AvailableFileResolver();
            var service = CreateService(
                db,
                reader,
                email,
                "Test",
                fileResolver: fileResolver,
                attachDecisionPdf: true);

            await service.RunAsync(CancellationToken.None);

            var message = Assert.Single(email.DirectMessages);
            Assert.Equal(new[] { EmailAutomationService.AllowedTestRecipient }, message.Recipients);
            Assert.DoesNotContain(EmailAutomationService.AmirEmail, message.Recipients);
            Assert.DoesNotContain(EmailAutomationService.YonatanEmail, message.Recipients);
            var attachment = Assert.Single(message.Attachments);
            Assert.Equal("decision.pdf", attachment.FileName);
            Assert.Equal("application/pdf", attachment.ContentType);
        }

        [Fact]
        public async Task ValidPdf_InLiveMode_QueuesToEmployeeWithAttachment()
        {
            await using var db = CreateDb();
            var reader = new FakeDocumentReader
            {
                Documents = { Decision(counter: 34, courtDocumentId: 134, tikCounter: 88, odDocId: 9103001) }
            };
            var email = new FakeEmailNotifier();
            var service = CreateService(
                db,
                reader,
                email,
                "Live",
                fileResolver: AvailableFileResolver(),
                attachDecisionPdf: true);

            await service.RunAsync(CancellationToken.None);

            var message = Assert.Single(email.DirectMessages);
            Assert.Equal(new[] { EmailAutomationService.AmirEmail }, message.Recipients);
            Assert.Single(message.Attachments);
        }

        [Fact]
        public async Task AttachmentFailure_DoesNotCauseDuplicateAlert()
        {
            await using var db = CreateDb();
            var reader = new FakeDocumentReader
            {
                Documents = { Decision(counter: 35, courtDocumentId: 135, odDocId: 9103001) }
            };
            var email = new FakeEmailNotifier();
            var fileResolver = new FakeDocumentFileResolver
            {
                ExceptionToThrow = new IOException("Unavailable.")
            };
            var service = CreateService(
                db,
                reader,
                email,
                "Test",
                fileResolver: fileResolver,
                attachDecisionPdf: true);

            await service.RunAsync(CancellationToken.None);
            await service.RunAsync(CancellationToken.None);

            Assert.Single(email.DirectMessages);
            Assert.Single(fileResolver.RequestedOdDocIds);
        }

        [Fact]
        public async Task OtherDocTypes_AreIgnoredEvenIfReaderReturnsThem()
        {
            await using var db = CreateDb();
            var reader = new FakeDocumentReader
            {
                Documents =
                {
                    new NetCourtDocument
                    {
                        Counter = 6,
                        TikCounter = 77,
                        CourtDocumentID = 106,
                        DocType = 4,
                        tsCreateDate = DateTime.UtcNow
                    }
                }
            };
            var email = new FakeEmailNotifier();
            var service = CreateService(db, reader, email, "Test");

            await service.RunAsync(CancellationToken.None);

            Assert.Empty(await db.NetCourtDecisionAlerts.ToListAsync());
            Assert.Empty(email.DirectMessages);
        }

        [Fact]
        public void DocumentIdentity_UsesRequiredPriority()
        {
            var document = new NetCourtDocument
            {
                Counter = 1,
                CourtDocumentID = 2,
                ODDocID = 3,
                DecisionID = 4
            };

            Assert.Equal(
                "CourtDocumentID:2",
                NetCourtDecisionAlertService.BuildDocumentIdentity(document));
            document.CourtDocumentID = null;
            Assert.Equal(
                "ODDocID:3",
                NetCourtDecisionAlertService.BuildDocumentIdentity(document));
            document.ODDocID = null;
            Assert.Equal(
                "DecisionID:4",
                NetCourtDecisionAlertService.BuildDocumentIdentity(document));
            document.DecisionID = null;
            Assert.Equal(
                "Counter:1",
                NetCourtDecisionAlertService.BuildDocumentIdentity(document));
        }

        [Fact]
        public async Task EmailNotifier_ExplicitRecipientsDoNotChangeCriticalGlobalRecipients()
        {
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Email:Enabled"] = "true",
                    ["Email:MaxEmailsPerHour"] = "10",
                    ["Email:Recipients:0"] = "global@odmon.example"
                })
                .Build();
            using var notifier = new EmailNotifier(
                NullLogger<EmailNotifier>.Instance,
                configuration);

            notifier.QueueCriticalAlert("critical", "body");
            Assert.True(notifier.QueueEmail(
                "direct",
                "body",
                new[] { "employee@odmon.example" }));

            var critical = await notifier.Reader.ReadAsync();
            var direct = await notifier.Reader.ReadAsync();

            Assert.Null(critical.Recipients);
            Assert.Null(critical.BccRecipients);
            Assert.Equal(new[] { "employee@odmon.example" }, direct.Recipients);
        }

        [Theory]
        [InlineData("Live", EmailAutomationService.AmirEmail)]
        [InlineData("Test", EmailAutomationService.AllowedTestRecipient)]
        public async Task NetCourtEmail_ConfiguredBccDoesNotReplacePrimaryRecipient(
            string emailMode,
            string expectedRecipient)
        {
            await using var db = CreateDb();
            var reader = new FakeDocumentReader
            {
                Documents = { Decision(counter: 50, courtDocumentId: 150, tikCounter: 88) }
            };
            var email = new FakeEmailNotifier();
            var service = CreateService(
                db,
                reader,
                email,
                emailMode,
                bccRecipients: new[] { EmailAutomationService.AllowedTestRecipient });

            await service.RunAsync(CancellationToken.None);

            var message = Assert.Single(email.DirectMessages);
            Assert.Equal(new[] { expectedRecipient }, message.Recipients);
            Assert.Equal(new[] { EmailAutomationService.AllowedTestRecipient }, message.BccRecipients);
        }

        [Fact]
        public void WorkerOperationalFailure_UsesCriticalAlertInfrastructure()
        {
            var email = new FakeEmailNotifier();
            var exception = new InvalidOperationException("SQL unavailable");

            Workers.NetCourtDecisionAlertWorker.QueueOperationalFailure(email, exception);

            var alert = Assert.Single(email.CriticalAlerts);
            Assert.Equal("NetCourt Decision Alert Failure", alert.AlertType);
            Assert.Equal(nameof(Workers.NetCourtDecisionAlertWorker), alert.Source);
            Assert.Contains("SQL unavailable", alert.Body);
            Assert.Empty(email.DirectMessages);
        }

        private static NetCourtDecisionAlertService CreateService(
            IntegrationDbContext db,
            FakeDocumentReader reader,
            FakeEmailNotifier email,
            string emailMode,
            FakeCaseResolver? resolver = null,
            int maxBatchSize = 100,
            string startFromDocDate = "2026-06-07",
            FakeDocumentFileResolver? fileResolver = null,
            bool attachDecisionPdf = false,
            string[]? bccRecipients = null)
        {
            resolver ??= new FakeCaseResolver
            {
                Cases =
                {
                    new OdcanitCase
                    {
                        TikCounter = 77,
                        TikNumber = "99/91005",
                        ClientVisualID = "2\\123"
                    },
                    new OdcanitCase
                    {
                        TikCounter = 88,
                        TikNumber = "98/91006",
                        ClientVisualID = "5\\456"
                    }
                }
            };

            var settings = new NetCourtDecisionAlertSettings
            {
                Enabled = true,
                StartFromDocDate = startFromDocDate,
                MaxBatchSize = maxBatchSize,
                AttachDecisionPdf = attachDecisionPdf,
                EmailMode = emailMode,
                TestRecipient = EmailAutomationService.AllowedTestRecipient,
                BccRecipients = bccRecipients ?? Array.Empty<string>(),
                FallbackRecipientEnabled = false,
                ClientNumberToRecipientEmail = new Dictionary<int, string>
                {
                    [2] = EmailAutomationService.YonatanEmail,
                    [15] = EmailAutomationService.YonatanEmail,
                    [5] = EmailAutomationService.AmirEmail,
                    [8] = EmailAutomationService.AmirEmail,
                    [3] = EmailAutomationService.AmirEmail,
                    [23] = EmailAutomationService.AmirEmail,
                    [253] = EmailAutomationService.AmirEmail,
                    [101] = EmailAutomationService.AmirEmail
                }
            };
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Email:Recipients:0"] = "global@odmon.example"
                })
                .Build();
            fileResolver ??= new FakeDocumentFileResolver();

            return new NetCourtDecisionAlertService(
                db,
                reader,
                resolver,
                fileResolver,
                email,
                configuration,
                Options.Create(settings),
                NullLogger<NetCourtDecisionAlertService>.Instance);
        }

        private static IntegrationDbContext CreateDb()
        {
            var options = new DbContextOptionsBuilder<IntegrationDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
                .Options;
            return new IntegrationDbContext(options);
        }

        private static FakeDocumentFileResolver AvailableFileResolver()
            => new()
            {
                Result = new NetCourtDocumentFileResult(
                    @"\\dc22\Odlight\Docs\case\decision.pdf",
                    "decision.pdf",
                    true,
                    1024,
                    null)
            };

        private static NetCourtDocument Decision(
            long counter,
            long courtDocumentId,
            int tikCounter = 77,
            DateTime? createdAtUtc = null,
            DateTime? docDate = null,
            long? odDocId = null)
            => new()
            {
                Counter = counter,
                TikCounter = tikCounter,
                CourtDocumentID = courtDocumentId,
                ODDocID = odDocId,
                DocType = 2,
                DocDate = docDate ?? createdAtUtc ?? DateTime.UtcNow,
                tsCreateDate = createdAtUtc ?? DateTime.UtcNow
            };

        private sealed class FakeDocumentReader : INetCourtDocumentReader
        {
            public List<NetCourtDocument> Documents { get; } = new();
            public DateTime? LastStartFromDocDate { get; private set; }

            public Task<List<NetCourtDocument>> GetDecisionDocumentsFromDocDateAsync(
                DateTime startFromDocDate,
                CancellationToken ct)
            {
                LastStartFromDocDate = startFromDocDate;
                return Task.FromResult(Documents
                    .Where(x =>
                        x.DocType is 2 or 3 &&
                        x.DocDate.HasValue &&
                        x.DocDate.Value.Date >= startFromDocDate.Date)
                    .OrderBy(x => x.DocDate)
                    .ThenBy(x => x.Counter)
                    .ToList());
            }
        }

        private sealed class FakeCaseResolver : INetCourtCaseResolver
        {
            public List<OdcanitCase> Cases { get; } = new();

            public Task<List<OdcanitCase>> GetCasesByTikCountersAsync(
                IEnumerable<int> tikCounters,
                CancellationToken ct)
            {
                var set = tikCounters.ToHashSet();
                return Task.FromResult(Cases.Where(x => set.Contains(x.TikCounter)).ToList());
            }
        }

        private sealed class FakeEmailNotifier : IEmailNotifier
        {
            public List<DirectMessage> DirectMessages { get; } = new();
            public List<CriticalAlert> CriticalAlerts { get; } = new();

            public bool QueueEmail(
                string subject,
                string body,
                IReadOnlyCollection<string> recipients,
                bool isHtml = false,
                IReadOnlyCollection<EmailAttachmentDescriptor>? attachments = null,
                IReadOnlyCollection<string>? bccRecipients = null)
            {
                DirectMessages.Add(new DirectMessage(
                    subject,
                    body,
                    recipients.ToArray(),
                    bccRecipients?.ToArray() ?? Array.Empty<string>(),
                    attachments?.ToArray() ?? Array.Empty<EmailAttachmentDescriptor>()));
                return true;
            }

            public void QueueCriticalAlert(
                string subject,
                string body,
                string? exceptionType = null,
                string? source = null,
                string? alertType = null,
                string? environmentName = null,
                string? serverName = null)
            {
                CriticalAlerts.Add(new CriticalAlert(
                    subject,
                    body,
                    source,
                    alertType));
            }

            public Task SendDailySummaryAsync(
                string subject,
                string htmlBody,
                CancellationToken ct)
                => Task.CompletedTask;

            public Task SendDigestAsync(
                string subject,
                string htmlBody,
                CancellationToken ct)
                => Task.CompletedTask;
        }

        private sealed record DirectMessage(
            string Subject,
            string Body,
            string[] Recipients,
            string[] BccRecipients,
            EmailAttachmentDescriptor[] Attachments);

        private sealed record CriticalAlert(
            string Subject,
            string Body,
            string? Source,
            string? AlertType);

        private sealed class FakeDocumentFileResolver : INetCourtDocumentFileResolver
        {
            public NetCourtDocumentFileResult Result { get; set; } =
                NetCourtDocumentFileResult.Unavailable("Unavailable in test.");
            public Exception? ExceptionToThrow { get; set; }
            public List<long?> RequestedOdDocIds { get; } = new();

            public Task<NetCourtDocumentFileResult> ResolveAsync(
                long? odDocId,
                string? tikNumber,
                CancellationToken ct)
            {
                RequestedOdDocIds.Add(odDocId);
                if (ExceptionToThrow != null)
                {
                    throw ExceptionToThrow;
                }

                return Task.FromResult(Result);
            }
        }
    }
}
