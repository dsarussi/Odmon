using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Odmon.Worker.Monday;
using Odmon.Worker.Security;
using Xunit;

namespace Odmon.Worker.Tests;

public sealed class MondayClientStatusReadTests
{
    [Fact]
    public async Task GetItemStatusValue_ReadsIdentityStateAndDisplayedLabelWithoutMutation()
    {
        const long boardId = 7000000001;
        const long itemId = 7000000002;
        const string columnId = "synthetic_status";
        var handler = new RecordingHandler(
            $"{{\"data\":{{\"items\":[{{\"id\":\"{itemId}\",\"state\":\"active\",\"board\":{{\"id\":\"{boardId}\"}},\"column_values\":[{{\"id\":\"{columnId}\",\"text\":\"Synthetic label\"}}]}}]}}}}");
        var client = new MondayClient(
            new HttpClient(handler) { BaseAddress = new Uri("https://example.invalid/") },
            new ConfigurationBuilder().Build(),
            new FakeSecretProvider(),
            NullLogger<MondayClient>.Instance);

        var result = await client.GetItemStatusValueAsync(
            boardId,
            itemId,
            columnId,
            CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(boardId, result.BoardId);
        Assert.Equal(itemId, result.ItemId);
        Assert.Equal("active", result.State);
        Assert.Equal("Synthetic label", result.Label);
        Assert.Contains("query", handler.RequestBody, StringComparison.Ordinal);
        Assert.DoesNotContain("mutation", handler.RequestBody, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GetItemStatusValue_EmptySuccessfulItemsResultReturnsNull()
    {
        var handler = new RecordingHandler("{\"data\":{\"items\":[]}}");
        var client = CreateClient(handler);

        var result = await client.GetItemStatusValueAsync(
            7000000001,
            7000000002,
            "synthetic_status",
            CancellationToken.None);

        Assert.Null(result);
        Assert.Contains("query", handler.RequestBody, StringComparison.Ordinal);
        Assert.DoesNotContain("mutation", handler.RequestBody, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task UpdateHearingStatus_WritesExactlyOneRequestedStatusColumn()
    {
        const long boardId = 7000000001;
        const long itemId = 7000000002;
        const string columnId = "synthetic_status";
        const string label = "Synthetic label";
        var handler = new RecordingHandler(
            $"{{\"data\":{{\"change_multiple_column_values\":{{\"id\":\"{itemId}\"}}}}}}");
        var client = new MondayClient(
            new HttpClient(handler) { BaseAddress = new Uri("https://example.invalid/") },
            new ConfigurationBuilder().Build(),
            new FakeSecretProvider(),
            NullLogger<MondayClient>.Instance);

        await client.UpdateHearingStatusAsync(
            boardId,
            itemId,
            label,
            columnId,
            CancellationToken.None);

        using var request = JsonDocument.Parse(handler.RequestBody);
        var columnValuesJson = request.RootElement
            .GetProperty("variables")
            .GetProperty("columnVals")
            .GetString();
        using var columnValues = JsonDocument.Parse(columnValuesJson!);
        var properties = columnValues.RootElement.EnumerateObject().ToArray();
        var onlyColumn = Assert.Single(properties);
        Assert.Equal(columnId, onlyColumn.Name);
        Assert.Equal(label, onlyColumn.Value.GetProperty("label").GetString());
    }

    [Fact]
    public async Task UpdateHearingStatus_Http200WithGraphQlErrorsThrowsClassifiedFailure()
    {
        const long boardId = 7000000001;
        const long itemId = 7000000002;
        var handler = new RecordingHandler(
            $"{{\"data\":{{\"change_multiple_column_values\":{{\"id\":\"{itemId}\"}}}},\"errors\":[{{\"message\":\"Synthetic rejection\",\"extensions\":{{\"code\":\"ColumnValueException\"}}}}]}}");
        var client = CreateClient(handler);

        var exception = await Assert.ThrowsAsync<MondayApiException>(() =>
            client.UpdateHearingStatusAsync(
                boardId,
                itemId,
                "Synthetic label",
                "synthetic_status",
                CancellationToken.None));

        Assert.Equal("COLUMNVALUEEXCEPTION", exception.ErrorCode);
        Assert.Equal(200, exception.HttpStatusCode);
        Assert.False(exception.IsRetryableRateLimit());
    }

    [Fact]
    public async Task UpdateHearingStatus_Http429GraphQlThrottleCapturesRetryDelay()
    {
        var handler = new RecordingHandler(
            "{\"errors\":[{\"message\":\"Synthetic throttle\",\"extensions\":{\"code\":\"COMPLEXITY_BUDGET_EXHAUSTED\",\"retry_in_seconds\":6}}]}",
            HttpStatusCode.TooManyRequests);
        var client = CreateClient(handler);

        var exception = await Assert.ThrowsAsync<MondayApiException>(() =>
            client.UpdateHearingStatusAsync(
                7000000001,
                7000000002,
                "Synthetic label",
                "synthetic_status",
                CancellationToken.None));

        Assert.True(exception.IsRetryableRateLimit());
        Assert.Equal("COMPLEXITY_BUDGET_EXHAUSTED", exception.ErrorCode);
        Assert.Equal(TimeSpan.FromSeconds(6), exception.RetryAfter);
    }

    [Fact]
    public async Task UpdateHearingStatus_HttpFailureWithoutGraphQlErrorsIsRejected()
    {
        var handler = new RecordingHandler(
            "{\"data\":null}",
            HttpStatusCode.ServiceUnavailable);
        var client = CreateClient(handler);

        var exception = await Assert.ThrowsAsync<MondayApiException>(() =>
            client.UpdateHearingStatusAsync(
                7000000001,
                7000000002,
                "Synthetic label",
                "synthetic_status",
                CancellationToken.None));

        Assert.Equal("HTTP_503", exception.ErrorCode);
        Assert.Equal(503, exception.HttpStatusCode);
    }

    [Fact]
    public async Task UpdateHearingStatus_MismatchedMutationIdentityIsRejected()
    {
        var handler = new RecordingHandler(
            "{\"data\":{\"change_multiple_column_values\":{\"id\":\"7000000999\"}}}");
        var client = CreateClient(handler);

        var exception = await Assert.ThrowsAsync<MondayApiException>(() =>
            client.UpdateHearingStatusAsync(
                7000000001,
                7000000002,
                "Synthetic label",
                "synthetic_status",
                CancellationToken.None));

        Assert.Equal("INVALID_MUTATION_RESPONSE", exception.ErrorCode);
    }

    private static MondayClient CreateClient(RecordingHandler handler)
        => new(
            new HttpClient(handler) { BaseAddress = new Uri("https://example.invalid/") },
            new ConfigurationBuilder().Build(),
            new FakeSecretProvider(),
            NullLogger<MondayClient>.Instance);

    private sealed class RecordingHandler(
        string responseBody,
        HttpStatusCode statusCode = HttpStatusCode.OK) : HttpMessageHandler
    {
        public string RequestBody { get; private set; } = string.Empty;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestBody = await request.Content!.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(responseBody, Encoding.UTF8, "application/json")
            };
        }
    }

    private sealed class FakeSecretProvider : ISecretProvider
    {
        public string? GetSecret(string key) => "synthetic-token";
        public Task<string?> GetSecretAsync(string key, CancellationToken ct = default)
            => Task.FromResult<string?>("synthetic-token");
    }
}
