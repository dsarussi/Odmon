namespace Odmon.Worker.Services
{
    public interface IEmailAutomationGraphClient
    {
        Task<EmailAutomationDeltaPage> GetDeltaPageAsync(
            string mailbox,
            string folderId,
            string? deltaLink,
            DateTime processingFromUtc,
            int pageSize,
            CancellationToken cancellationToken);

        Task ForwardMessageAsync(
            string mailbox,
            string graphMessageId,
            string targetEmail,
            string? comment,
            CancellationToken cancellationToken);
    }

    public sealed record EmailAutomationDeltaPage(
        IReadOnlyList<EmailAutomationMessage> Messages,
        string? NextLink,
        string? DeltaLink);

    public sealed record EmailAutomationMessage(
        string Id,
        string? InternetMessageId,
        string? Subject,
        string? Sender,
        IReadOnlyList<string> ToRecipients,
        IReadOnlyList<string> CcRecipients,
        DateTime? ReceivedDateTimeUtc,
        bool Removed = false);

    public sealed class GraphThrottledException : Exception
    {
        public GraphThrottledException(TimeSpan? retryAfter)
            : base("Microsoft Graph throttled the email automation request.")
        {
            RetryAfter = retryAfter;
        }

        public TimeSpan? RetryAfter { get; }
    }
}
