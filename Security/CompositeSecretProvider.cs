using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Odmon.Worker.Security;

public class CompositeSecretProvider : ISecretProvider
{
    private readonly IReadOnlyList<ISecretProvider> _providers;

    public CompositeSecretProvider(IReadOnlyList<ISecretProvider> providers)
    {
        _providers = providers ?? throw new ArgumentNullException(nameof(providers));
    }

    public string? GetSecret(string key)
    {
        foreach (var provider in _providers)
        {
            var value = provider.GetSecret(key);
            if (!string.IsNullOrEmpty(value))
            {
                return value;
            }
        }

        return null;
    }

    public async Task<string?> GetSecretAsync(string key, CancellationToken ct = default)
    {
        foreach (var provider in _providers)
        {
            var value = await provider.GetSecretAsync(key, ct).ConfigureAwait(false);
            if (!string.IsNullOrEmpty(value))
            {
                return value;
            }
        }

        return null;
    }
}

