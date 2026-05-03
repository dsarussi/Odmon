namespace Odmon.Worker.Voicenter
{
    /// <summary>
    /// Thrown when Voicenter responds with a weekly-usage / quota-exhausted error.
    /// Caller should stop issuing further CallHistoryDetail requests for the cycle.
    /// </summary>
    public sealed class VoicenterQuotaExceededException : Exception
    {
        public string EndpointType { get; }
        public int? HttpStatus { get; }
        public string? CallId { get; }
        public string? ResponseBodySnippet { get; }

        public VoicenterQuotaExceededException(
            string endpointType,
            int? httpStatus,
            string? callId,
            string? responseBodySnippet,
            string message)
            : base(message)
        {
            EndpointType = endpointType;
            HttpStatus = httpStatus;
            CallId = callId;
            ResponseBodySnippet = responseBodySnippet;
        }
    }
}
