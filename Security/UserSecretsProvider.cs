using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;

namespace Odmon.Worker.Security;

public class UserSecretsProvider : ISecretProvider
{
    private readonly IConfiguration _configuration;

    public UserSecretsProvider(IConfiguration configuration)
    {
        _configuration = configuration;
    }

    public string? GetSecret(string key)
    {
        return _configuration[$"Secrets:{key}"];
    }

    public Task<string?> GetSecretAsync(string key, CancellationToken ct = default)
    {
        return Task.FromResult(GetSecret(key));
    }
}

