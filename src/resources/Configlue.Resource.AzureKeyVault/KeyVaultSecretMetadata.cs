namespace Configlue.Resource.AzureKeyVault;

/// <summary>Safe Key Vault secret metadata carried as provenance and revision information.</summary>
/// <remarks>Never contains secret values; only the name, version, enabled state, and timestamps.</remarks>
public sealed record KeyVaultSecretMetadata
{
    /// <summary>Creates secret metadata.</summary>
    public KeyVaultSecretMetadata(
        string name,
        string? version,
        bool enabled,
        DateTimeOffset? createdOn = null,
        DateTimeOffset? updatedOn = null
    )
    {
        Name = KeyVaultSecretName.Validate(name, nameof(name));
        if (version is not null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(version);
        }

        Name = name;
        Version = version;
        Enabled = enabled;
        CreatedOn = createdOn;
        UpdatedOn = updatedOn;
    }

    /// <summary>The secret name.</summary>
    public string Name { get; init; }

    /// <summary>The secret version identifier, when known.</summary>
    public string? Version { get; init; }

    /// <summary>Whether the secret version is enabled.</summary>
    public bool Enabled { get; init; }

    /// <summary>When the secret version was created, when known.</summary>
    public DateTimeOffset? CreatedOn { get; init; }

    /// <summary>When the secret version was last updated, when known.</summary>
    public DateTimeOffset? UpdatedOn { get; init; }

    /// <inheritdoc />
    public override string ToString() =>
        $"KeyVaultSecret(name={Name}, version={Version ?? "<current>"}, enabled={Enabled})";
}

/// <summary>A secret value together with its safe metadata.</summary>
public sealed record KeyVaultSecretResult
{
    /// <summary>Creates a secret result.</summary>
    public KeyVaultSecretResult(string value, KeyVaultSecretMetadata metadata)
    {
        ArgumentNullException.ThrowIfNull(value);
        ArgumentNullException.ThrowIfNull(metadata);
        Value = value;
        Metadata = metadata;
    }

    /// <summary>The secret value. Never surface this through diagnostics.</summary>
    public string Value { get; init; }

    /// <summary>Safe metadata for provenance and revision tracking.</summary>
    public KeyVaultSecretMetadata Metadata { get; init; }

    /// <inheritdoc />
    public override string ToString() => $"KeyVaultSecret(value=REDACTED, {Metadata})";
}
