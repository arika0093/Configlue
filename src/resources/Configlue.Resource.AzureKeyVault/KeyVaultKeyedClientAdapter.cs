using Configlue.Sources;

namespace Configlue.Resource.AzureKeyVault;

/// <summary>
/// Adapts the Azure Key Vault transport to the shared keyed-secret contract.
/// </summary>
/// <remarks>Never includes secret values in thrown messages.</remarks>
internal sealed class KeyVaultKeyedClientAdapter : IKeyedSecretClient
{
    private readonly IKeyVaultSecretClient _client;

    internal KeyVaultKeyedClientAdapter(IKeyVaultSecretClient client)
    {
        ArgumentNullException.ThrowIfNull(client);
        _client = client;
    }

    public async ValueTask<KeyedSecretValue?> GetAsync(
        string key,
        string? version,
        CancellationToken cancellationToken = default
    )
    {
        KeyVaultSecretResult secret;
        try
        {
            secret = await _client
                .GetSecretAsync(key, version, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (KeyVaultSecretNotFoundException)
        {
            return null;
        }
        catch (KeyVaultSecretUnavailableException exception)
        {
            throw new KeyedSecretUnavailableException(
                $"The Key Vault secret '{key}' is temporarily unavailable.",
                exception
            );
        }

        return new KeyedSecretValue(secret.Value, secret.Metadata.Version, secret.Metadata.Enabled);
    }

    public async ValueTask<string?> SetAsync(
        string key,
        string value,
        CancellationToken cancellationToken = default
    )
    {
        try
        {
            var metadata = await _client
                .SetSecretAsync(key, value, cancellationToken)
                .ConfigureAwait(false);
            return metadata.Version;
        }
        catch (KeyVaultSecretUnavailableException exception)
        {
            throw new KeyedSecretUnavailableException(
                $"The Key Vault secret '{key}' is temporarily unavailable.",
                exception
            );
        }
    }
}
