namespace Configlue.Resource.AzureKeyVault;

/// <summary>Maps one Configlue member path to one Key Vault secret name.</summary>
/// <remarks>
/// Property paths are canonical dotted member paths (for example <c>Database.Host</c>),
/// matched case-insensitively against the generated model schema. Secret names must satisfy
/// the Key Vault rule documented in <see cref="KeyVaultSecretName"/>.
/// </remarks>
public sealed record KeyVaultSecretMapping
{
    /// <summary>Creates a mapping.</summary>
    public KeyVaultSecretMapping(string propertyPath, string secretName, string? version = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(propertyPath);
        KeyVaultSecretName.Validate(secretName);
        if (version is not null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(version);
        }

        PropertyPath = propertyPath;
        SecretName = secretName;
        Version = version;
    }

    /// <summary>The canonical dotted member path.</summary>
    public string PropertyPath { get; init; }

    /// <summary>The Key Vault secret name.</summary>
    public string SecretName { get; init; }

    /// <summary>A fixed secret version for this member, or null for the current version.</summary>
    public string? Version { get; init; }

    /// <inheritdoc />
    public override string ToString() =>
        $"KeyVaultSecretMapping(path={PropertyPath}, secret={SecretName}, version={Version ?? "<current>"})";
}
