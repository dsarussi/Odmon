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

    /// <summary>
    /// Body-enabled, attachment-free Graph reader used only by EmailFiling.
    /// It is separate so the existing forwarding Graph payload is unchanged.
    /// </summary>
    public interface IEmailFilingGraphClient
    {
        Task<EmailAutomationDeltaPage> GetDeltaPageAsync(
            string mailbox,
            string folderId,
            string? deltaLink,
            DateTime processingFromUtc,
            int pageSize,
            CancellationToken cancellationToken);

        /// <summary>
        /// Retrieves the complete RFC 822/MIME representation only after the
        /// lightweight filing pipeline has selected at least one write target.
        /// </summary>
        Task<byte[]> GetMimeAsync(
            string mailbox,
            string messageId,
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
        bool Removed = false,
        string? Body = null,
        string? BodyContentType = null,
        IReadOnlyList<string>? BccRecipients = null,
        DateTime? SentDateTimeUtc = null);

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
