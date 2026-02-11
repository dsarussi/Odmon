using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Odmon.Worker.Monday;
using Odmon.Worker.Security;
using Xunit;

namespace Odmon.Worker.Tests
{
    /// <summary>
    /// Tests for MondayMetadataProvider board-level metadata caching.
    /// Verifies that board column metadata is cached per boardId with TTL,
    /// and that all downstream methods (dropdown labels, status labels,
    /// column type, column-by-title) share the same cached API call.
    /// </summary>
    public class MondayMetadataProviderCacheTests
    {
        private const long TestBoardId = 5035534500;
        private const string TestColumnId = "dropdown_test_col";
        private const string TestColumnId2 = "dropdown_test_col2";

        // ====================================================================
        // 1. Same boardId called twice → underlying API called once
        // ====================================================================

        [Fact]
        public async Task SameBoardId_CalledTwice_ApiCalledOnce()
        {
            var handler = new FakeHttpMessageHandler(MakeBoardResponse(
                (TestColumnId, "dropdown", MakeDropdownSettings("Label1", "Label2"), "Col1")));
            var provider = CreateProvider(handler);

            var result1 = await provider.GetBoardColumnsMetadataAsync(TestBoardId);
            var result2 = await provider.GetBoardColumnsMetadataAsync(TestBoardId);

            Assert.Equal(1, handler.CallCount);
            Assert.Single(result1);
            Assert.Single(result2);
            Assert.Equal("dropdown", result1[TestColumnId].ColumnType);
        }

        // ====================================================================
        // 2. Dropdown labels for two columns on same board → single API call
        // ====================================================================

        [Fact]
        public async Task TwoDropdownColumns_SameBoard_SingleApiCall()
        {
            var handler = new FakeHttpMessageHandler(MakeBoardResponse(
                (TestColumnId, "dropdown", MakeDropdownSettings("Alpha", "Beta"), "Col1"),
                (TestColumnId2, "dropdown", MakeDropdownSettings("Gamma", "Delta"), "Col2")));
            var provider = CreateProvider(handler);

            var result1 = await provider.GetAllowedDropdownLabelsAsync(TestBoardId, TestColumnId);
            var result2 = await provider.GetAllowedDropdownLabelsAsync(TestBoardId, TestColumnId2);

            Assert.Equal(1, handler.CallCount); // both columns served from single board cache
            Assert.Contains("Alpha", result1);
            Assert.Contains("Beta", result1);
            Assert.Contains("Gamma", result2);
            Assert.Contains("Delta", result2);
        }

        // ====================================================================
        // 3. GetColumnTypeAsync uses board cache (no separate API call)
        // ====================================================================

        [Fact]
        public async Task GetColumnType_UsesBoardCache()
        {
            var handler = new FakeHttpMessageHandler(MakeBoardResponse(
                (TestColumnId, "dropdown", MakeDropdownSettings("Label1"), "Col1"),
                ("numeric_col", "numeric", "{}", "Numeric Col")));
            var provider = CreateProvider(handler);

            // First call: populates board cache
            var labels = await provider.GetAllowedDropdownLabelsAsync(TestBoardId, TestColumnId);
            Assert.Equal(1, handler.CallCount);

            // Second call: GetColumnTypeAsync reuses board cache
            var colType = await provider.GetColumnTypeAsync(TestBoardId, "numeric_col");
            Assert.Equal(1, handler.CallCount); // no additional API call
            Assert.Equal("numeric", colType);
        }

        // ====================================================================
        // 4. GetColumnIdByTitleAsync uses board cache
        // ====================================================================

        [Fact]
        public async Task GetColumnIdByTitle_UsesBoardCache()
        {
            var handler = new FakeHttpMessageHandler(MakeBoardResponse(
                (TestColumnId, "dropdown", MakeDropdownSettings("Label1"), "My Column Title")));
            var provider = CreateProvider(handler);

            var colId = await provider.GetColumnIdByTitleAsync(TestBoardId, "My Column Title");
            Assert.Equal(TestColumnId, colId);
            Assert.Equal(1, handler.CallCount);

            // Call again with same title: board cache hit
            var colId2 = await provider.GetColumnIdByTitleAsync(TestBoardId, "My Column Title");
            Assert.Equal(TestColumnId, colId2);
            Assert.Equal(1, handler.CallCount);
        }

        // ====================================================================
        // 5. TTL expiration → API called again
        // ====================================================================

        [Fact]
        public async Task TtlExpired_ApiCalledAgain()
        {
            var handler = new FakeHttpMessageHandler(MakeBoardResponse(
                (TestColumnId, "dropdown", MakeDropdownSettings("Label1"), "Col1")));
            var provider = CreateProvider(handler);

            // First call → API called
            await provider.GetBoardColumnsMetadataAsync(TestBoardId);
            Assert.Equal(1, handler.CallCount);

            // Manually expire the board cache entry by backdating the timestamp
            lock (provider.BoardMetadataCache)
            {
                if (provider.BoardMetadataCache.TryGetValue(TestBoardId, out var entry))
                {
                    entry.Timestamp = DateTime.UtcNow.AddMinutes(-15); // well past 10-min TTL
                }
            }

            // Second call → cache expired → API called again
            await provider.GetBoardColumnsMetadataAsync(TestBoardId);
            Assert.Equal(2, handler.CallCount);
        }

        // ====================================================================
        // 6. Failed metadata call does not poison cache
        // ====================================================================

        [Fact]
        public async Task FailedCall_DoesNotPoisonCache()
        {
            // First call: failure (500 error)
            var failHandler = new FakeHttpMessageHandler(statusCode: HttpStatusCode.InternalServerError);
            var provider = CreateProvider(failHandler);

            await Assert.ThrowsAsync<HttpRequestException>(
                () => provider.GetBoardColumnsMetadataAsync(TestBoardId));

            Assert.Equal(1, failHandler.CallCount);

            // Verify cache is empty (failure not cached)
            lock (provider.BoardMetadataCache)
            {
                Assert.False(provider.BoardMetadataCache.ContainsKey(TestBoardId));
            }
        }

        // ====================================================================
        // 7. Failed then success → success is cached
        // ====================================================================

        [Fact]
        public async Task FailedThenSuccess_SuccessCached()
        {
            var responses = new Queue<(string? body, HttpStatusCode status)>();
            responses.Enqueue((null, HttpStatusCode.InternalServerError));
            responses.Enqueue((MakeBoardResponse(
                (TestColumnId, "dropdown", MakeDropdownSettings("Label1"), "Col1")), HttpStatusCode.OK));

            var handler = new FakeHttpMessageHandler(responses);
            var provider = CreateProvider(handler);

            // First call fails
            await Assert.ThrowsAsync<HttpRequestException>(
                () => provider.GetBoardColumnsMetadataAsync(TestBoardId));
            Assert.Equal(1, handler.CallCount);

            // Second call succeeds
            var result = await provider.GetBoardColumnsMetadataAsync(TestBoardId);
            Assert.Equal(2, handler.CallCount);
            Assert.Contains(TestColumnId, result.Keys);

            // Third call is cached
            var result2 = await provider.GetBoardColumnsMetadataAsync(TestBoardId);
            Assert.Equal(2, handler.CallCount); // still 2
            Assert.Contains(TestColumnId, result2.Keys);
        }

        // ====================================================================
        // 8. Dropdown labels still work correctly after board cache hit
        // ====================================================================

        [Fact]
        public async Task DropdownLabels_BoardCacheHit_ParsesCorrectly()
        {
            var handler = new FakeHttpMessageHandler(MakeBoardResponse(
                (TestColumnId, "dropdown", MakeDropdownSettings("Label1", "Label2"), "Col1")));
            var provider = CreateProvider(handler);

            var labels1 = await provider.GetAllowedDropdownLabelsAsync(TestBoardId, TestColumnId);
            var labels2 = await provider.GetAllowedDropdownLabelsAsync(TestBoardId, TestColumnId);

            Assert.Equal(1, handler.CallCount);
            Assert.Equal(2, labels1.Count);
            Assert.Equal(2, labels2.Count);
            Assert.Contains("Label1", labels1);
            Assert.Contains("Label2", labels2);
        }

        // ====================================================================
        // Helpers
        // ====================================================================

        private static MondayMetadataProvider CreateProvider(FakeHttpMessageHandler handler)
        {
            var client = new HttpClient(handler) { BaseAddress = new Uri("https://api.monday.com/v2") };
            var config = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Monday:ApiToken"] = "test-token-for-caching-tests"
                })
                .Build();
            var secretProvider = new FakeSecretProvider("test-token-for-caching-tests");
            var logger = NullLogger<MondayMetadataProvider>.Instance;
            return new MondayMetadataProvider(client, config, secretProvider, logger);
        }

        /// <summary>
        /// Builds a valid Monday API JSON response with multiple columns, each having id, type, settings_str, and title.
        /// </summary>
        private static string MakeBoardResponse(params (string columnId, string type, string settingsStr, string title)[] columns)
        {
            var columnsList = new List<object>();
            foreach (var (columnId, type, settingsStr, title) in columns)
            {
                columnsList.Add(new
                {
                    id = columnId,
                    type = type,
                    settings_str = settingsStr,
                    title = title
                });
            }

            var response = new
            {
                data = new
                {
                    boards = new[]
                    {
                        new
                        {
                            id = TestBoardId.ToString(),
                            columns = columnsList
                        }
                    }
                }
            };

            return JsonSerializer.Serialize(response);
        }

        /// <summary>
        /// Builds a JSON settings_str for a dropdown column with the specified label names.
        /// </summary>
        private static string MakeDropdownSettings(params string[] labelNames)
        {
            var labelsArray = new List<object>();
            foreach (var label in labelNames)
            {
                labelsArray.Add(new { name = label });
            }
            return JsonSerializer.Serialize(new { labels = labelsArray });
        }

        // ====================================================================
        // Fakes
        // ====================================================================

        private class FakeSecretProvider : ISecretProvider
        {
            private readonly string _token;
            public FakeSecretProvider(string token) => _token = token;
            public string? GetSecret(string key) => key == "Monday__ApiToken" ? _token : null;
            public Task<string?> GetSecretAsync(string key, CancellationToken ct = default) => Task.FromResult(GetSecret(key));
        }

        /// <summary>
        /// Fake HTTP handler that returns configurable responses and tracks call count.
        /// Supports single response, queued responses, and error status codes.
        /// </summary>
        private class FakeHttpMessageHandler : HttpMessageHandler
        {
            private readonly Queue<(string? body, HttpStatusCode status)> _responses = new();
            private readonly string? _defaultBody;
            private readonly HttpStatusCode _defaultStatus;
            public int CallCount { get; private set; }

            /// <summary>Single response that is returned for every call.</summary>
            public FakeHttpMessageHandler(string responseBody)
            {
                _defaultBody = responseBody;
                _defaultStatus = HttpStatusCode.OK;
            }

            /// <summary>Single error status code for every call.</summary>
            public FakeHttpMessageHandler(HttpStatusCode statusCode)
            {
                _defaultBody = null;
                _defaultStatus = statusCode;
            }

            /// <summary>Queue of response bodies (each dequeued in order).</summary>
            public FakeHttpMessageHandler(Queue<string> responseBodies)
            {
                foreach (var body in responseBodies)
                    _responses.Enqueue((body, HttpStatusCode.OK));
                _defaultBody = null;
                _defaultStatus = HttpStatusCode.OK;
            }

            /// <summary>Queue of (body, status) pairs.</summary>
            public FakeHttpMessageHandler(Queue<(string? body, HttpStatusCode status)> responses)
            {
                _responses = responses;
                _defaultBody = null;
                _defaultStatus = HttpStatusCode.OK;
            }

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                CallCount++;

                string? body;
                HttpStatusCode status;

                if (_responses.Count > 0)
                {
                    var entry = _responses.Dequeue();
                    body = entry.body;
                    status = entry.status;
                }
                else
                {
                    body = _defaultBody;
                    status = _defaultStatus;
                }

                var response = new HttpResponseMessage(status);
                if (body != null)
                {
                    response.Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json");
                }
                else
                {
                    response.Content = new StringContent("Internal Server Error", System.Text.Encoding.UTF8, "text/plain");
                }

                return Task.FromResult(response);
            }
        }
    }
}
