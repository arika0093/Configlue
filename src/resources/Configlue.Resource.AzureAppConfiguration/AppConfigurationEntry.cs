namespace Configlue.Resource.AzureAppConfiguration;

/// <summary>
/// One Azure App Configuration key-value with the metadata needed for provenance and concurrency.
/// </summary>
/// <remarks>
/// Values and content types are preserved faithfully. Key Vault references
/// (<c>application/vnd.microsoft.appconfig.keyvaultref+json</c>) are kept as opaque
/// strings and are never resolved by this provider. Compose with the Azure Key Vault
/// provider when resolution is required.
/// </remarks>
public sealed class AppConfigurationEntry
{
    /// <summary>Creates an entry.</summary>
    public AppConfigurationEntry(
        string key,
        string? label,
        string? value,
        string? contentType,
        string? eTag,
        DateTimeOffset? lastModified = null
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        Key = key;
        Label = label;
        Value = value;
        ContentType = contentType;
        ETag = eTag;
        LastModified = lastModified;
    }

    /// <summary>The configuration key.</summary>
    public string Key { get; }

    /// <summary>The label; null means the unlabeled entry.</summary>
    public string? Label { get; }

    /// <summary>The raw string value; preserved verbatim.</summary>
    public string? Value { get; }

    /// <summary>The content type; preserved verbatim.</summary>
    public string? ContentType { get; }

    /// <summary>The ETag used for provenance and optimistic concurrency.</summary>
    public string? ETag { get; }

    /// <summary>The last-modified timestamp, when provided by the service.</summary>
    public DateTimeOffset? LastModified { get; }
}
