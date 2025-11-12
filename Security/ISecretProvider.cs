using System.Threading;
using System.Threading.Tasks;

namespace Odmon.Worker.Security;

public interface ISecretProvider
{
    string? GetSecret(string key);

    Task<string?> GetSecretAsync(string key, CancellationToken ct = default);
}

