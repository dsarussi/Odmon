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
        public async Task FirstRun_StoresMaxCounter_WithoutHistoricalRows()
        {
            await using var db = CreateDb();
            var reader = new FakeDocumentReader
            {
                MaxCounter = 299746,
                Documents = { Decision(counter: 1, courtDocumentId: 101) }
            };
            var email = new FakeEmailNotifier();
            var service = CreateService(db, reader, email, "Test");

            await service.RunAsync(CancellationToken.None);

            Assert.Equal(1, reader.MaxCounterCallCount);
            Assert.Equal(0, reader.BatchCallCount);
            Assert.Empty(await db.NetCourtDecisionAlerts.ToListAsync());
            Assert.Equal(
                299746,
                (await db.NetCourtDecisionAlertStates.SingleAsync()).LastSeenCounter);
        }

        [Fact]
        public async Task FirstRun_DoesNotQueueEmail()
        {
            await using var db = CreateDb();
            var reader = new FakeDocumentReader
            {
                MaxCounter = 2,
                Documents = { Decision(counter: 2, courtDocumentId: 102) }
            };
            var email = new FakeEmailNotifier();
            var service = CreateService(db, reader, email, "Test");

            await service.RunAsync(CancellationToken.None);

            Assert.Empty(email.DirectMessages);
            Assert.Empty(await db.NetCourtDecisionAlerts.ToListAsync());
        }

        [Fact]
        public async Task LaterRun_ProcessesOnlyCountersAboveWatermark()
        {
            await using var db = CreateDb();
            await MarkWatermarkAsync(db, 20);
            var reader = new FakeDocumentReader
            {
                Documents =
                {
                    Decision(counter: 19, courtDocumentId: 119),
                    Decision(counter: 20, courtDocumentId: 120),
                    Decision(counter: 21, courtDocumentId: 121)
                }
            };
            var email = new FakeEmailNotifier();
            var service = CreateService(db, reader, email, "Test");

            var result = await service.RunAsync(CancellationToken.None);

            Assert.Equal(20, reader.LastRequestedCounter);
            Assert.Equal(1, result.CandidatesDetected);
            Assert.Equal(
                21,
                (await db.NetCourtDecisionAlerts.SingleAsync()).NetCourtCounter);
            Assert.Equal(
                21,
                (await db.NetCourtDecisionAlertStates.SingleAsync()).LastSeenCounter);
        }

        [Fact]
        public async Task NoRows_DoesNotChangeWatermarkState()
        {
            await using var db = CreateDb();
            var originalUpdatedAtUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            db.NetCourtDecisionAlertStates.Add(new NetCourtDecisionAlertState
            {
                Id = 1,
                LastSeenCounter = 20,
                BaselineCompletedAtUtc = originalUpdatedAtUtc,
                UpdatedAtUtc = originalUpdatedAtUtc
            });
            await db.SaveChangesAsync();
            var reader = new FakeDocumentReader();
            var service = CreateService(db, reader, new FakeEmailNotifier(), "Test");

            await service.RunAsync(CancellationToken.None);

            var state = await db.NetCourtDecisionAlertStates.SingleAsync();
            Assert.Equal(20, state.LastSeenCounter);
            Assert.Equal(originalUpdatedAtUtc, state.UpdatedAtUtc);
        }

        [Fact]
        public async Task QueryUsesMaxBatchSize_AndAdvancesToHandledBatchHighCounter()
        {
            await using var db = CreateDb();
            await MarkWatermarkAsync(db, 20);
            var reader = new FakeDocumentReader
            {
                Documents =
                {
                    Decision(counter: 21, courtDocumentId: 121),
                    Decision(counter: 22, courtDocumentId: 122),
                    Decision(counter: 23, courtDocumentId: 123)
                }
            };
            var email = new FakeEmailNotifier();
            var service = CreateService(db, reader, email, "Test", maxBatchSize: 2);

            var result = await service.RunAsync(CancellationToken.None);

            Assert.Equal(2, reader.LastMaxBatchSize);
            Assert.Equal(2, result.EmailsQueued);
            Assert.Equal(2, await db.NetCourtDecisionAlerts.CountAsync());
            Assert.Equal(
                22,
                (await db.NetCourtDecisionAlertStates.SingleAsync()).LastSeenCounter);
        }

        [Fact]
        public async Task RowsAreProcessedInAscendingCounterOrder()
        {
            await using var db = CreateDb();
            await MarkWatermarkAsync(db, 100);
            var reader = new FakeDocumentReader
            {
                Documents =
                {
                    Decision(counter: 103, courtDocumentId: 103, tikCounter: 103),
                    Decision(counter: 101, courtDocumentId: 101, tikCounter: 101),
                    Decision(counter: 102, courtDocumentId: 102, tikCounter: 102)
                }
            };
            var resolver = new FakeCaseResolver
            {
                Cases =
                {
                    RoutedCase(101),
                    RoutedCase(102),
                    RoutedCase(103)
                }
            };
            var email = new FakeEmailNotifier();
            var service = CreateService(db, reader, email, "Test", resolver);

            await service.RunAsync(CancellationToken.None);

            Assert.Equal(
                new[] { "9/101", "9/102", "9/103" },
                email.DirectMessages.Select(x => x.Subject.Split(' ').Last()).ToArray());
        }

        [Fact]
        public async Task TimestampAndDocDateDoNotAffectCounterDetection()
        {
            await using var db = CreateDb();
            await MarkWatermarkAsync(db, 200);
            var reader = new FakeDocumentReader
            {
                Documents =
                {
                    Decision(
                        counter: 202,
                        courtDocumentId: 202,
                        createdAtUtc: DateTime.Today,
                        docDate: DateTime.Today.AddYears(-5)),
                    Decision(
                        counter: 201,
                        courtDocumentId: 201,
                        createdAtUtc: DateTime.Today,
                        docDate: DateTime.Today.AddYears(5))
                }
            };
            var email = new FakeEmailNotifier();
            var service = CreateService(db, reader, email, "Test");

            await service.RunAsync(CancellationToken.None);

            Assert.Equal(2, email.DirectMessages.Count);
            Assert.Equal(
                202,
                (await db.NetCourtDecisionAlertStates.SingleAsync()).LastSeenCounter);
        }

        [Fact]
        public async Task NewDecision_InTestMode_QueuesOnceOnlyToTestRecipient()
        {
            await using var db = CreateDb();
            await MarkWatermarkAsync(db, 0);
            var reader = new FakeDocumentReader
            {
                Documents = { Decision(counter: 3, courtDocumentId: 103, tikCounter: 77) }
            };
            var email = new FakeEmailNotifier();
            var service = CreateService(db, reader, email, "Test");

            await service.RunAsync(CancellationToken.None);
            await service.RunAsync(CancellationToken.None);

            var message = Assert.Single(email.DirectMessages);
            Assert.Equal(new[] { "odmon@ezer-law.com" }, message.Recipients);
            Assert.DoesNotContain("amir@ezer-law.com", message.Recipients);
            Assert.DoesNotContain("yonatan@ezer-law.com", message.Recipients);
            Assert.Equal("החלטה חדשה בתיק 9/1984", message.Subject);
            Assert.Contains("שלום יונתן", message.Body);
            Assert.Contains("מצב בדיקה - המייל המקורי היה מיועד אל: yonatan@ezer-law.com", message.Body);

            var tracked = await db.NetCourtDecisionAlerts.SingleAsync();
            Assert.Equal("yonatan@ezer-law.com", tracked.IntendedRecipientEmail);
            Assert.Equal("odmon@ezer-law.com", tracked.ActualRecipientEmail);
            Assert.Equal(NetCourtDecisionAlertStatuses.TestEmailQueued, tracked.Status);
        }

        [Fact]
        public async Task NewDecision_InLiveMode_QueuesToRoutedEmployee()
        {
            await using var db = CreateDb();
            await MarkWatermarkAsync(db, 0);
            var reader = new FakeDocumentReader
            {
                Documents = { Decision(counter: 4, courtDocumentId: 104, tikCounter: 88) }
            };
            var email = new FakeEmailNotifier();
            var service = CreateService(db, reader, email, "Live");

            await service.RunAsync(CancellationToken.None);

            var message = Assert.Single(email.DirectMessages);
            Assert.Equal(new[] { "amir@ezer-law.com" }, message.Recipients);
            Assert.Equal("שלום אמיר" + Environment.NewLine + Environment.NewLine +
                         "התקבלה החלטה חדשה בתיק - 5/2000", message.Body);
            Assert.DoesNotContain("מצב בדיקה", message.Body);
            Assert.Equal(
                NetCourtDecisionAlertStatuses.LiveEmailQueued,
                (await db.NetCourtDecisionAlerts.SingleAsync()).Status);
        }

        [Fact]
        public async Task MissingRouting_IsRecordedAndDoesNotQueueOrCrash()
        {
            await using var db = CreateDb();
            await MarkWatermarkAsync(db, 0);
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
            var service = CreateService(db, reader, email, "Live", resolver);

            var result = await service.RunAsync(CancellationToken.None);

            Assert.Equal(1, result.MissingRouting);
            Assert.Empty(email.DirectMessages);
            Assert.Equal(
                NetCourtDecisionAlertStatuses.MissingRouting,
                (await db.NetCourtDecisionAlerts.SingleAsync()).Status);
            Assert.Equal(
                5,
                (await db.NetCourtDecisionAlertStates.SingleAsync()).LastSeenCounter);
        }

        [Fact]
        public async Task OtherDocTypes_AreIgnoredEvenIfReaderReturnsThem()
        {
            await using var db = CreateDb();
            await MarkWatermarkAsync(db, 0);
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
                    ["Email:Recipients:0"] = "global@example.com"
                })
                .Build();
            using var notifier = new EmailNotifier(
                NullLogger<EmailNotifier>.Instance,
                configuration);

            notifier.QueueCriticalAlert("critical", "body");
            Assert.True(notifier.QueueEmail(
                "direct",
                "body",
                new[] { "employee@example.com" }));

            var critical = await notifier.Reader.ReadAsync();
            var direct = await notifier.Reader.ReadAsync();

            Assert.Null(critical.Recipients);
            Assert.Equal(new[] { "employee@example.com" }, direct.Recipients);
        }

        private static NetCourtDecisionAlertService CreateService(
            IntegrationDbContext db,
            FakeDocumentReader reader,
            FakeEmailNotifier email,
            string emailMode,
            FakeCaseResolver? resolver = null,
            int maxBatchSize = 100)
        {
            resolver ??= new FakeCaseResolver
            {
                Cases =
                {
                    new OdcanitCase
                    {
                        TikCounter = 77,
                        TikNumber = "9/1984",
                        ClientVisualID = "2\\123"
                    },
                    new OdcanitCase
                    {
                        TikCounter = 88,
                        TikNumber = "5/2000",
                        ClientVisualID = "5\\456"
                    }
                }
            };

            var settings = new NetCourtDecisionAlertSettings
            {
                Enabled = true,
                MaxBatchSize = maxBatchSize,
                EmailMode = emailMode,
                TestRecipient = "odmon@ezer-law.com",
                FallbackRecipientEnabled = false,
                ClientNumberToRecipientEmail = new Dictionary<int, string>
                {
                    [2] = "yonatan@ezer-law.com",
                    [15] = "yonatan@ezer-law.com",
                    [5] = "amir@ezer-law.com",
                    [8] = "amir@ezer-law.com",
                    [3] = "amir@ezer-law.com",
                    [23] = "amir@ezer-law.com",
                    [253] = "amir@ezer-law.com"
                }
            };
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Email:Recipients:0"] = "global@example.com"
                })
                .Build();

            return new NetCourtDecisionAlertService(
                db,
                reader,
                resolver,
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

        private static async Task MarkWatermarkAsync(
            IntegrationDbContext db,
            long lastSeenCounter)
        {
            db.NetCourtDecisionAlertStates.Add(new NetCourtDecisionAlertState
            {
                Id = 1,
                LastSeenCounter = lastSeenCounter,
                BaselineCompletedAtUtc = DateTime.UtcNow,
                UpdatedAtUtc = DateTime.UtcNow
            });
            await db.SaveChangesAsync();
        }

        private static OdcanitCase RoutedCase(int tikCounter)
            => new()
            {
                TikCounter = tikCounter,
                TikNumber = $"9/{tikCounter}",
                ClientVisualID = "2\\123"
            };

        private static NetCourtDocument Decision(
            long counter,
            long courtDocumentId,
            int tikCounter = 77,
            DateTime? createdAtUtc = null,
            DateTime? docDate = null)
            => new()
            {
                Counter = counter,
                TikCounter = tikCounter,
                CourtDocumentID = courtDocumentId,
                DocType = 2,
                DocDate = docDate ?? createdAtUtc ?? DateTime.UtcNow,
                tsCreateDate = createdAtUtc ?? DateTime.UtcNow
            };

        private sealed class FakeDocumentReader : INetCourtDocumentReader
        {
            public List<NetCourtDocument> Documents { get; } = new();
            public long MaxCounter { get; set; }
            public int MaxCounterCallCount { get; private set; }
            public int BatchCallCount { get; private set; }
            public long? LastRequestedCounter { get; private set; }
            public int? LastMaxBatchSize { get; private set; }

            public Task<long> GetMaxDecisionCounterAsync(CancellationToken ct)
            {
                MaxCounterCallCount++;
                return Task.FromResult(MaxCounter);
            }

            public Task<List<NetCourtDocument>> GetDecisionDocumentsAfterCounterAsync(
                long lastSeenCounter,
                int maxBatchSize,
                CancellationToken ct)
            {
                BatchCallCount++;
                LastRequestedCounter = lastSeenCounter;
                LastMaxBatchSize = maxBatchSize;
                return Task.FromResult(Documents
                    .Where(x => x.DocType is 2 or 3 && x.Counter > lastSeenCounter)
                    .OrderBy(x => x.Counter)
                    .Take(maxBatchSize)
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

            public bool QueueEmail(
                string subject,
                string body,
                IReadOnlyCollection<string> recipients,
                bool isHtml = false)
            {
                DirectMessages.Add(new DirectMessage(
                    subject,
                    body,
                    recipients.ToArray()));
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
            string[] Recipients);
    }
}
