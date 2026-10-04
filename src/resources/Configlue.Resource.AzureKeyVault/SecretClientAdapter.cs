using Azure;
using Azure.Security.KeyVault.Secrets;

namespace Configlue.Resource.AzureKeyVault;

/// <summary>Adapts the Azure SDK <c>SecretClient</c> to the internal transport.</summary>
/// <remarks>The supplied client remains caller-owned and is never disposed by Configlue.</remarks>
internal sealed class SecretClientAdapter : IKeyVaultSecretClient
{
    private readonly SecretClient _client;

    /// <summary>Creates an adapter over an externally owned client.</summary>
    internal SecretClientAdapter(SecretClient client)
    {
        ArgumentNullException.ThrowIfNull(client);
        _client = client;
    }

    /// <summary>The wrapped SDK client.</summary>
    internal SecretClient InnerClient => _client;

    /// <inheritdoc />
    public async ValueTask<KeyVaultSecretResult> GetSecretAsync(
        string secretName,
        string? version,
        CancellationToken cancellationToken = default
    )
    {
        KeyVaultSecretName.Validate(secretName);
        try
        {
            var response = await _client
                .GetSecretAsync(secretName, version, cancellationToken)
                .ConfigureAwait(false);
            return CreateResult(response.Value);
        }
        catch (RequestFailedException exception)
        {
            throw ConvertRequestFailure(_client.VaultUri, secretName, exception);
        }
    }

    /// <inheritdoc />
    public async ValueTask<KeyVaultSecretMetadata> SetSecretAsync(
        string secretName,
        string secretValue,
        CancellationToken cancellationToken = default
    )
    {
        KeyVaultSecretName.Validate(secretName);
        ArgumentNullException.ThrowIfNull(secretValue);
        try
        {
            var response = await _client
                .SetSecretAsync(secretName, secretValue, cancellationToken)
                .ConfigureAwait(false);
            return CreateMetadata(response.Value.Properties);
        }
        catch (RequestFailedException exception)
        {
            throw ConvertRequestFailure(_client.VaultUri, secretName, exception);
        }
    }

    internal static KeyVaultSecretResult CreateResult(KeyVaultSecret secret)
    {
        ArgumentNullException.ThrowIfNull(secret);
        return new KeyVaultSecretResult(
            secret.Value ?? string.Empty,
            CreateMetadata(secret.Properties)
        );
    }

    internal static KeyVaultSecretMetadata CreateMetadata(SecretProperties properties)
    {
        ArgumentNullException.ThrowIfNull(properties);
        return new KeyVaultSecretMetadata(
            properties.Name,
            properties.Version,
            properties.Enabled ?? true,
            properties.CreatedOn,
            properties.UpdatedOn
        );
    }

    internal static KeyVaultSecretException ConvertRequestFailure(
        Uri? vaultUri,
        string secretName,
        RequestFailedException exception
    )
    {
        var vaultHost = vaultUri?.Host ?? "<vault>";
        var message =
            $"Azure Key Vault request for secret '{secretName}' on '{vaultHost}' failed with status {exception.Status}.";
        if (exception.Status == 404)
        {
            return new KeyVaultSecretNotFoundException(message, exception);
        }

        if (
            exception.Status == 408
            || exception.Status == 429
            || (exception.Status >= 500 && exception.Status <= 599)
        )
        {
            return new KeyVaultSecretUnavailableException(message, exception);
        }

        return new KeyVaultSecretException(message, exception);
    }

    /// <inheritdoc />
    public override string ToString() => "SecretClientAdapter(client=REDACTED)";
}
