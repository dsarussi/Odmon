namespace Odmon.Worker.OdcanitAccess
{
    public interface INetCourtDocumentFileResolver
    {
        Task<NetCourtDocumentFileResult> ResolveAsync(
            long? odDocId,
            string? tikNumber,
            CancellationToken ct);
    }

    public sealed record NetCourtDocumentFileResult(
        string? FilePath,
        string? FileName,
        bool Exists,
        long? FileSizeBytes,
        string? ErrorMessage)
    {
        public bool IsAvailable =>
            Exists &&
            !string.IsNullOrWhiteSpace(FilePath) &&
            string.IsNullOrWhiteSpace(ErrorMessage);

        public static NetCourtDocumentFileResult Unavailable(string errorMessage)
            => new(null, null, false, null, errorMessage);
    }
}
