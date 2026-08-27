using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Azure.Core;
using Azure.Identity;
using Microsoft.Extensions.Options;
using Odmon.Worker.Configuration;

namespace Odmon.Worker.Services
{
    public sealed class MicrosoftGraphEmailAutomationClient :
        IEmailAutomationGraphClient,
        IEmailFilingGraphClient
    {
        private static readonly string[] GraphScopes = ["https://graph.microsoft.com/.default"];
        private readonly HttpClient _httpClient;
        private readonly EmailAutomationSettings _settings;
        private readonly TokenCredential _credential;

        public MicrosoftGraphEmailAutomationClient(
            HttpClient httpClient,
            IOptions<EmailAutomationSettings> options)
        {
            _httpClient = httpClient;
            _settings = options.Value;
            _credential = new ClientSecretCredential(
                _settings.TenantId,
                _settings.ClientId,
                _settings.ClientSecret);
        }

        public Task<EmailAutomationDeltaPage> GetDeltaPageAsync(
            string mailbox,
            string folderId,
            string? deltaLink,
            DateTime processingFromUtc,
            int pageSize,
            CancellationToken cancellationToken)
            => GetDeltaPageCoreAsync(
                mailbox,
                folderId,
                deltaLink,
                processingFromUtc,
                pageSize,
                includeBody: false,
                cancellationToken);

        Task<EmailAutomationDeltaPage> IEmailFilingGraphClient.GetDeltaPageAsync(
            string mailbox,
            string folderId,
            string? deltaLink,
            DateTime processingFromUtc,
            int pageSize,
            CancellationToken cancellationToken)
            => GetDeltaPageCoreAsync(
                mailbox,
                folderId,
                deltaLink,
                processingFromUtc,
                pageSize,
                includeBody: true,
                cancellationToken);

        async Task<byte[]> IEmailFilingGraphClient.GetMimeAsync(
            string mailbox,
            string messageId,
            CancellationToken cancellationToken)
        {
            var requestUrl =
                $"users/{Uri.EscapeDataString(mailbox)}/messages/{Uri.EscapeDataString(messageId)}/$value";
            using var request = new HttpRequestMessage(HttpMethod.Get, requestUrl);
            request.Headers.Accept.ParseAdd("message/rfc822");
            await AddAuthorizationAsync(request, cancellationToken);
            using var response = await _httpClient.SendAsync(request, cancellationToken);
            ThrowIfUnsuccessful(response);
            return await response.Content.ReadAsByteArrayAsync(cancellationToken);
        }

        private async Task<EmailAutomationDeltaPage> GetDeltaPageCoreAsync(
            string mailbox,
            string folderId,
            string? deltaLink,
            DateTime processingFromUtc,
            int pageSize,
            bool includeBody,
            CancellationToken cancellationToken)
        {
            var requestUrl = deltaLink;
            if (string.IsNullOrWhiteSpace(requestUrl))
            {
                // The filing reader selects message content but never requests
                // attachments or inline-attachment collections. Forwarding keeps
                // its original header-only select.
                var select = GetMessageSelect(includeBody);
                var filterTime = Uri.EscapeDataString(processingFromUtc.ToUniversalTime().ToString("O"));
                requestUrl =
                    $"users/{Uri.EscapeDataString(mailbox)}/mailFolders/{Uri.EscapeDataString(folderId)}/messages/delta" +
                    $"?changeType=created&$select={select}&$filter=receivedDateTime%20gt%20{filterTime}";
            }

            using var request = new HttpRequestMessage(HttpMethod.Get, requestUrl);
            request.Headers.TryAddWithoutValidation("Prefer", $"odata.maxpagesize={Math.Max(1, pageSize)}");
            await AddAuthorizationAsync(request, cancellationToken);

            using var response = await _httpClient.SendAsync(request, cancellationToken);
            ThrowIfUnsuccessful(response);

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            var root = document.RootElement;
            var messages = new List<EmailAutomationMessage>();

            if (root.TryGetProperty("value", out var values))
            {
                foreach (var value in values.EnumerateArray())
                {
                    var removed = value.TryGetProperty("@removed", out _);
                    var body = includeBody ? GetBody(value) : (null, null);
                    messages.Add(new EmailAutomationMessage(
                        GetString(value, "id") ?? string.Empty,
                        GetString(value, "internetMessageId"),
                        GetString(value, "subject"),
                        includeBody
                            ? GetEmailAddress(value, "sender") ?? GetEmailAddress(value, "from")
                            : GetEmailAddress(value, "from"),
                        GetRecipients(value, "toRecipients"),
                        GetRecipients(value, "ccRecipients"),
                        GetDateTime(value, "receivedDateTime"),
                        removed,
                        body.Content,
                        body.ContentType,
                        includeBody ? GetRecipients(value, "bccRecipients") : null,
                        includeBody ? GetDateTime(value, "sentDateTime") : null));
                }
            }

            return new EmailAutomationDeltaPage(
                messages,
                GetString(root, "@odata.nextLink"),
                GetString(root, "@odata.deltaLink"));
        }

        public async Task ForwardMessageAsync(
            string mailbox,
            string graphMessageId,
            string targetEmail,
            string? comment,
            CancellationToken cancellationToken)
        {
            var url =
                $"users/{Uri.EscapeDataString(mailbox)}/messages/{Uri.EscapeDataString(graphMessageId)}/forward";
            var payload = JsonSerializer.Serialize(new
            {
                comment = comment ?? string.Empty,
                toRecipients = new[]
                {
                    new { emailAddress = new { address = targetEmail } }
                }
            });

            using var request = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json")
            };
            await AddAuthorizationAsync(request, cancellationToken);

            using var response = await _httpClient.SendAsync(request, cancellationToken);
            ThrowIfUnsuccessful(response);
        }

        private async Task AddAuthorizationAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var token = await _credential.GetTokenAsync(
                new TokenRequestContext(GraphScopes),
                cancellationToken);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);
        }

        private static void ThrowIfUnsuccessful(HttpResponseMessage response)
        {
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                TimeSpan? retryAfter = response.Headers.RetryAfter?.Delta;
                if (retryAfter == null &&
                    response.Headers.RetryAfter?.Date is DateTimeOffset retryDate)
                {
                    retryAfter = retryDate - DateTimeOffset.UtcNow;
                }

                throw new GraphThrottledException(retryAfter);
            }

            if (response.IsSuccessStatusCode)
            {
                return;
            }

            throw new HttpRequestException(
                $"Microsoft Graph returned HTTP {(int)response.StatusCode}.",
                null,
                response.StatusCode);
        }

        private static string? GetString(JsonElement element, string propertyName)
            => element.TryGetProperty(propertyName, out var value) &&
               value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;

        private static DateTime? GetDateTime(JsonElement element, string propertyName)
            => DateTime.TryParse(
                GetString(element, propertyName),
                null,
                System.Globalization.DateTimeStyles.AdjustToUniversal,
                out var value)
                ? value.ToUniversalTime()
                : null;

        private static string? GetEmailAddress(JsonElement element, string propertyName)
        {
            if (!element.TryGetProperty(propertyName, out var recipient) ||
                !recipient.TryGetProperty("emailAddress", out var emailAddress))
            {
                return null;
            }

            return GetString(emailAddress, "address");
        }

        private static IReadOnlyList<string> GetRecipients(JsonElement element, string propertyName)
        {
            if (!element.TryGetProperty(propertyName, out var recipients) ||
                recipients.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            return recipients.EnumerateArray()
                .Select(GetNestedAddress)
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Cast<string>()
                .ToArray();
        }

        private static string? GetNestedAddress(JsonElement recipient)
            => recipient.TryGetProperty("emailAddress", out var emailAddress)
                ? GetString(emailAddress, "address")
                : null;

        private static (string? Content, string? ContentType) GetBody(JsonElement element)
        {
            if (!element.TryGetProperty("body", out var body) ||
                body.ValueKind != JsonValueKind.Object)
            {
                return (null, null);
            }

            return (GetString(body, "content"), GetString(body, "contentType"));
        }

        internal static string GetMessageSelect(bool includeBody)
            => includeBody
                ? "id,internetMessageId,subject,from,sender,toRecipients,ccRecipients,bccRecipients,receivedDateTime,sentDateTime,body"
                : "id,internetMessageId,subject,from,toRecipients,ccRecipients,receivedDateTime";
    }
}
