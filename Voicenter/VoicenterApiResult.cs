namespace Odmon.Worker.Voicenter
{
    /// <summary>
    /// Wraps a Voicenter API call so callers see HTTP status and quota-detection metadata,
    /// not just the parsed payload.
    /// </summary>
    public sealed class VoicenterApiResult<T>
    {
        public T? Data { get; init; }
        public int? HttpStatus { get; init; }
        public bool Success { get; init; }
        public bool QuotaExceeded { get; init; }
        public string? ErrorMessage { get; init; }
        public string? ResponseBodySnippet { get; init; }

        public static VoicenterApiResult<T> Ok(T data, int httpStatus) =>
            new() { Data = data, HttpStatus = httpStatus, Success = true };

        public static VoicenterApiResult<T> Failure(int? httpStatus, string error, string? snippet = null) =>
            new() { HttpStatus = httpStatus, Success = false, ErrorMessage = error, ResponseBodySnippet = snippet };

        public static VoicenterApiResult<T> Quota(int? httpStatus, string error, string? snippet) =>
            new() { HttpStatus = httpStatus, Success = false, QuotaExceeded = true, ErrorMessage = error, ResponseBodySnippet = snippet };
    }
}
