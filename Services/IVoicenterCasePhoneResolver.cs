using Odmon.Worker.Voicenter;

namespace Odmon.Worker.Services
{
    public interface IVoicenterCasePhoneResolver
    {
        string ScopeName { get; }

        Task<IReadOnlyList<CasePhoneMatch>> FindCasesByPhoneAsync(string normalizedPhone, CancellationToken ct);
    }
}
