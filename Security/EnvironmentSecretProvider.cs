using System;
using System.Threading;
using System.Threading.Tasks;

namespace Odmon.Worker.Security;

public class EnvironmentSecretProvider : ISecretProvider
{
    public string? GetSecret(string key)
    {
        return Environment.GetEnvironmentVariable(key);
    }

    public Task<string?> GetSecretAsync(string key, CancellationToken ct = default)
    {
        return Task.FromResult(GetSecret(key));
    }
}

