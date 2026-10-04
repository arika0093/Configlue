namespace Configlue.Resource.AzureKeyVault;

/// <summary>Options for a resource backed by one Azure Key Vault secret.</summary>
public sealed class KeyVaultSecretResourceOptions
{
    /// <summary>
    /// An advanced fixed identity override shared by every operation context. Configuring this
    /// asserts that all selected secrets share one physical coordination domain; an
    /// incorrect value can make batch grouping unsafe.
    /// </summary>
    public ResourceId? FixedResourceId { get; init; }

    /// <summary>Resolves the secret name for each subject-aware operation.</summary>
    /// <remarks>Selectors should return stable, Key Vault-legal names for a given context.</remarks>
    public Func<ConfiglueResourceContext, string>? SecretNameSelector { get; init; }

    /// <summary>Resolves the fixed secret version for each subject-aware operation.</summary>
    /// <remarks>A null result selects the current version. Fixed versions are immutable.</remarks>
    public Func<ConfiglueResourceContext, string?>? SecretVersionSelector { get; init; }
}
