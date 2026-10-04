namespace Configlue.Resource.AzureKeyVault;

/// <summary>
/// A client-compatible abstraction over Azure Key Vault secrets.
/// Inject a fake in tests; the production adapter wraps <c>SecretClient</c>.
/// </summary>
/// <remarks>Implementations must never include secret values in thrown messages.</remarks>
public interface IKeyVaultSecretClient
{
    /// <summary>
    /// Gets one secret value with safe metadata. Throws
    /// <see cref="KeyVaultSecretNotFoundException"/> when the name or version is missing
    /// (or not visible), and <see cref="KeyVaultSecretUnavailableException"/> when throttled
    /// or transiently unavailable.
    /// </summary>
    /// <param name="secretName">The validated Key Vault secret name.</param>
    /// <param name="version">A fixed version, or null for the current version.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    ValueTask<KeyVaultSecretResult> GetSecretAsync(
        string secretName,
        string? version,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Creates or updates one secret value according to Key Vault upsert semantics.
    /// This operation is unconditional; it provides no compare-and-swap.
    /// </summary>
    /// <param name="secretName">The validated Key Vault secret name.</param>
    /// <param name="secretValue">The new secret value. Never captured in diagnostics.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    ValueTask<KeyVaultSecretMetadata> SetSecretAsync(
        string secretName,
        string secretValue,
        CancellationToken cancellationToken = default
    );
}
