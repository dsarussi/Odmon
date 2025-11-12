using System;
using System.Threading;
using System.Threading.Tasks;
using Azure;
using Azure.Security.KeyVault.Secrets;

namespace Odmon.Worker.Security;

public class AzureKeyVaultSecretProvider : ISecretProvider
{
    private readonly SecretClient _secretClient;

    public AzureKeyVaultSecretProvider(SecretClient secretClient)
    {
        _secretClient = secretClient;
    }

    public string? GetSecret(string key)
    {
        try
        {
            return GetSecretAsync(key).GetAwaiter().GetResult();
        }
        catch (InvalidOperationException)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }

    public async Task<string?> GetSecretAsync(string key, CancellationToken ct = default)
    {
        try
        {
            var response = await _secretClient.GetSecretAsync(key, cancellationToken: ct);
            return response.Value?.Value;
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            return null;
        }
    }
}

