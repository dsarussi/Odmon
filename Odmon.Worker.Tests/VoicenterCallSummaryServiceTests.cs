using System.Net;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Odmon.Worker.Configuration;
using Odmon.Worker.Data;
using Odmon.Worker.Models;
using Odmon.Worker.OdcanitAccess;
using Odmon.Worker.Services;
using Odmon.Worker.Voicenter;
using Xunit;

namespace Odmon.Worker.Tests
{
    public class VoicenterCallSummaryServiceTests
    {
        [Fact]
        public async Task MatchedCaseWithoutMondayMapping_WritesAnnexToOdcanit()
        {
            using var db = CreateDb();
            var writer = new FakeOdcanitWriter();
            var resolver = new FakePhoneResolver(new CasePhoneMatch
            {
                TikCounter = 91015,
                TikNumber = "99/91015",
                MatchedField = "PolicyHolderPhone",
            });
            var service = CreateService(db, writer, resolver, FakeHttpClientFactory.WithCalls(("call-1", "0541234567")));

            var result = await service.RunAsync("code", "token", CancellationToken.None);

            Assert.Equal(1, result.Written);
            Assert.Empty(db.MondayItemMappings);
            var write = Assert.Single(writer.Writes);
            Assert.Equal(91015, write.TikCounter);
            Assert.Equal("99/91015", write.TikNumber);
        }

        [Fact]
        public async Task MatchedCaseNotReadyForMonday_WritesAnnexToOdcanit()
        {
            using var db = CreateDb();
            var writer = new FakeOdcanitWriter();
            var resolver = new FakePhoneResolver(new CasePhoneMatch
            {
                TikCounter = 91003,
                TikNumber = "99/91003",
                MatchedField = "ThirdPartyPhone",
            });
            var service = CreateService(db, writer, resolver, FakeHttpClientFactory.WithCalls(("call-2", "0547654321")));

            var result = await service.RunAsync("code", "token", CancellationToken.None);

            Assert.Equal(1, result.Written);
            Assert.Single(writer.Writes);
            Assert.Equal("99/91003", writer.Writes[0].TikNumber);
        }

        [Fact]
        public async Task QuotaExceededState_IsRetriedLater_AndWritten()
        {
            using var db = CreateDb();
            db.VoicenterCallProcessingStates.Add(new VoicenterCallProcessingState
            {
                CallId = "quota-call",
                Status = VoicenterCallProcessingStatus.QuotaExceeded,
                FirstSeenUtc = DateTime.UtcNow.AddDays(-1),
                LastSeenUtc = DateTime.UtcNow.AddDays(-1),
                LastCheckedUtc = DateTime.UtcNow.AddDays(-1),
                Attempts = 1,
                LastError = "Cycle quota exceeded; detail fetch skipped",
            });
            await db.SaveChangesAsync();

            var writer = new FakeOdcanitWriter();
            var resolver = new FakePhoneResolver(new CasePhoneMatch
            {
                TikCounter = 9001,
                TikNumber = "9/001",
                MatchedField = "DriverPhone",
            });
            var service = CreateService(db, writer, resolver, FakeHttpClientFactory.WithDeferredDetail("quota-call", "0541112222"));

            var result = await service.RunAsync("code", "token", CancellationToken.None);

            Assert.Equal(1, result.Written);
            Assert.Single(writer.Writes);
            var state = await db.VoicenterCallProcessingStates.SingleAsync(s => s.CallId == "quota-call");
            Assert.Equal(VoicenterCallProcessingStatus.Written, state.Status);
        }

        [Fact]
        public async Task ExistingSuccessfulWrite_IsNotDuplicated()
        {
            using var db = CreateDb();
            db.NispahWriteLogs.Add(new NispahWriteLog
            {
                TikCounter = 9002,
                TikVisualId = "9/002",
                NispahType = "Call summary",
                SourceKind = VoicenterCallSummaryService.SourceKind,
                SourceItemId = VoicenterCallSummaryService.CallIdToSourceItemId("call-duplicate"),
                InfoHash = "existing",
                CreatedAtUtc = DateTime.UtcNow.AddDays(-1),
                Failed = false,
            });
            await db.SaveChangesAsync();

            var writer = new FakeOdcanitWriter();
            var resolver = new FakePhoneResolver(new CasePhoneMatch
            {
                TikCounter = 9002,
                TikNumber = "9/002",
                MatchedField = "DriverPhone",
            });
            var service = CreateService(db, writer, resolver, FakeHttpClientFactory.WithCalls(("call-duplicate", "0543334444")));

            var result = await service.RunAsync("code", "token", CancellationToken.None);

            Assert.Equal(0, result.Written);
            Assert.Empty(writer.Writes);
            Assert.Equal(1, result.SkippedDuplicateBeforeDetail);
        }

        [Fact]
        public async Task NoMatch_IsPersistedOnlyAfterFullOdcanitResolverReturnsNoCases()
        {
            using var db = CreateDb();
            var writer = new FakeOdcanitWriter();
            var resolver = new FakePhoneResolver();
            var service = CreateService(db, writer, resolver, FakeHttpClientFactory.WithCalls(("call-nomatch", "0549998888")));

            var result = await service.RunAsync("code", "token", CancellationToken.None);

            Assert.Equal(1, result.SkippedNoMatch);
            Assert.Empty(writer.Writes);
            Assert.Equal(1, resolver.SearchCount);
            var state = await db.VoicenterCallProcessingStates.SingleAsync(s => s.CallId == "call-nomatch");
            Assert.Equal(VoicenterCallProcessingStatus.NoMatch, state.Status);
            Assert.Contains(resolver.ScopeName, state.LastError);
        }

        private static VoicenterCallSummaryService CreateService(
            IntegrationDbContext db,
            FakeOdcanitWriter writer,
            IVoicenterCasePhoneResolver resolver,
            IHttpClientFactory httpClientFactory,
            VoicenterCallSummarySettings? settings = null)
        {
            settings ??= new VoicenterCallSummarySettings
            {
                OnlyAnsweredCalls = true,
                MinimumDurationSeconds = 10,
                ThrottleMs = 0,
                ReprocessQuotaExceededLookbackDays = 14,
                ReprocessNoMatchLookbackDays = 0,
                MaxDeferredReprocessCallsPerRun = 50,
                UsageWarningEmailEnabled = false,
                WeeklyUsageWarningThreshold = int.MaxValue,
            };

            var usage = new VoicenterUsageTracker(
                db,
                new FakeEmailNotifier(),
                Options.Create(settings),
                NullLogger<VoicenterUsageTracker>.Instance);

            return new VoicenterCallSummaryService(
                new VoicenterApiClient(httpClientFactory, NullLogger<VoicenterApiClient>.Instance),
                writer,
                db,
                usage,
                resolver,
                Options.Create(settings),
                Options.Create(new VoicenterBackfillSettings()),
                NullLogger<VoicenterCallSummaryService>.Instance);
        }

        private static IntegrationDbContext CreateDb()
        {
            var options = new DbContextOptionsBuilder<IntegrationDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
                .Options;
            return new IntegrationDbContext(options);
        }

        private sealed class FakePhoneResolver : IVoicenterCasePhoneResolver
        {
            private readonly IReadOnlyList<CasePhoneMatch> _matches;

            public FakePhoneResolver(params CasePhoneMatch[] matches)
            {
                _matches = matches;
            }

            public string ScopeName => "FakeFullOdcanitScope";
            public int SearchCount { get; private set; }

            public Task<IReadOnlyList<CasePhoneMatch>> FindCasesByPhoneAsync(string normalizedPhone, CancellationToken ct)
            {
                SearchCount++;
                return Task.FromResult(_matches);
            }
        }

        private sealed class FakeOdcanitWriter : IOdcanitWriter
        {
            public List<(int TikCounter, string TikNumber, string NispahType, string Info)> Writes { get; } = [];

            public Task AppendNispahAsync(OdcanitCase c, DateTime nowUtc, string nispahType, string info, CancellationToken ct)
            {
                Writes.Add((c.TikCounter, c.TikNumber, nispahType, info));
                return Task.CompletedTask;
            }
        }

        private sealed class FakeEmailNotifier : IEmailNotifier
        {
            public void QueueCriticalAlert(string subject, string body, string? exceptionType = null, string? source = null, string? alertType = null, string? environmentName = null, string? serverName = null)
            {
            }

            public bool QueueEmail(string subject, string body, IReadOnlyCollection<string> recipients, bool isHtml = false, IReadOnlyCollection<EmailAttachmentDescriptor>? attachments = null, IReadOnlyCollection<string>? bccRecipients = null) => true;

            public Task SendDailySummaryAsync(string subject, string htmlBody, CancellationToken ct) => Task.CompletedTask;

            public Task SendDigestAsync(string subject, string htmlBody, CancellationToken ct) => Task.CompletedTask;
        }

        private sealed class FakeHttpClientFactory : IHttpClientFactory
        {
            private readonly IReadOnlyDictionary<string, string> _detailsByCallId;
            private readonly IReadOnlyList<(string CallId, string Phone)> _cdrCalls;

            private FakeHttpClientFactory(
                IReadOnlyList<(string CallId, string Phone)> cdrCalls,
                IReadOnlyDictionary<string, string> detailsByCallId)
            {
                _cdrCalls = cdrCalls;
                _detailsByCallId = detailsByCallId;
            }

            public static FakeHttpClientFactory WithCalls(params (string CallId, string Phone)[] calls)
            {
                return new FakeHttpClientFactory(
                    calls,
                    calls.ToDictionary(c => c.CallId, c => BuildDetailJson(c.Phone)));
            }

            public static FakeHttpClientFactory WithDeferredDetail(string callId, string phone)
            {
                return new FakeHttpClientFactory(
                    [],
                    new Dictionary<string, string> { [callId] = BuildDetailJson(phone) });
            }

            public HttpClient CreateClient(string name)
            {
                return new HttpClient(new Handler(_cdrCalls, _detailsByCallId));
            }

            private static string BuildDetailJson(string phone)
            {
                return $$"""
                {
                  "Data": {
                    "cdr_data": {
                      "sec_total": 60,
                      "dialstatus_name": "ANSWER",
                      "client_phone": "{{phone}}",
                      "cdr_time": "2026-06-25T10:00:00Z"
                    },
                    "ai_data": {
                      "insights": {
                        "summary": "Call summary text"
                      }
                    }
                  }
                }
                """;
            }

            private sealed class Handler : HttpMessageHandler
            {
                private readonly IReadOnlyList<(string CallId, string Phone)> _cdrCalls;
                private readonly IReadOnlyDictionary<string, string> _detailsByCallId;

                public Handler(
                    IReadOnlyList<(string CallId, string Phone)> cdrCalls,
                    IReadOnlyDictionary<string, string> detailsByCallId)
                {
                    _cdrCalls = cdrCalls;
                    _detailsByCallId = detailsByCallId;
                }

                protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
                {
                    var uri = request.RequestUri?.ToString() ?? string.Empty;
                    if (uri.Contains("/hub/cdr/", StringComparison.OrdinalIgnoreCase))
                    {
                        var entries = string.Join(",", _cdrCalls.Select(c => $$"""
                        {
                          "CallID": "{{c.CallId}}",
                          "CallerNumber": "{{c.Phone}}",
                          "TargetNumber": "039999999",
                          "Date": "2026-06-25T10:00:00Z",
                          "Duration": 60,
                          "Type": "out",
                          "DialStatus": "ANSWER"
                        }
                        """));
                        return JsonResponse($$"""{"CDR_LIST":[{{entries}}]}""");
                    }

                    var callId = uri.Split('/').Last();
                    if (_detailsByCallId.TryGetValue(callId, out var detail))
                        return JsonResponse(detail);

                    return JsonResponse("""{"error":"not found"}""", HttpStatusCode.NotFound);
                }

                private static Task<HttpResponseMessage> JsonResponse(string json, HttpStatusCode statusCode = HttpStatusCode.OK)
                {
                    return Task.FromResult(new HttpResponseMessage(statusCode)
                    {
                        Content = new StringContent(json, Encoding.UTF8, "application/json"),
                    });
                }
            }
        }
    }
}
