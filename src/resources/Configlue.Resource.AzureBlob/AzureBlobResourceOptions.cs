using Azure.Storage.Blobs;

namespace Configlue.Resource.AzureBlob;

/// <summary>Options for a resource backed by one Azure Blob Storage blob.</summary>
public sealed class AzureBlobResourceOptions
{
    /// <summary>
    /// An advanced fixed identity override shared by every operation context. Configuring this
    /// asserts that all selected containers and blobs share one physical coordination domain; an
    /// incorrect value can make batch grouping unsafe.
    /// </summary>
    public ResourceId? FixedResourceId { get; init; }

    /// <summary>Resolves the container for each subject-aware operation.</summary>
    /// <remarks>Selectors should return stable values for a given context.</remarks>
    public Func<ConfiglueResourceContext, string>? ContainerNameSelector { get; init; }

    /// <summary>Resolves the blob name for each subject-aware operation.</summary>
    /// <remarks>Selectors should return stable values for a given context.</remarks>
    public Func<ConfiglueResourceContext, string>? BlobNameSelector { get; init; }

    /// <summary>
    /// Resolves a fully bound, externally owned blob client for each subject-aware operation.
    /// </summary>
    /// <remarks>
    /// The selected client fully determines the container and blob for the operation and cannot
    /// be combined with <see cref="ContainerNameSelector"/> or <see cref="BlobNameSelector"/>.
    /// Use a distinct route when the selected client points to a different storage account or
    /// container.
    /// </remarks>
    public Func<ConfiglueResourceContext, BlobClient>? BlobClientSelector { get; init; }

    /// <summary>The content type applied to uploaded blobs.</summary>
    /// <remarks>When null, uploads leave any existing content type untouched.</remarks>
    public string? ContentType { get; init; }

    /// <summary>User-defined metadata applied to uploaded blobs.</summary>
    /// <remarks>When null, uploads leave any existing metadata untouched.</remarks>
    public IDictionary<string, string>? Metadata { get; init; }

    /// <summary>
    /// Whether the resource watches for changes by polling blob properties (ETag metadata).
    /// Watching is opt-in because it issues periodic metadata requests.
    /// </summary>
    public bool EnableWatching { get; init; }

    /// <summary>The minimum interval between blob-property polls while waiting for changes.</summary>
    public TimeSpan WatchPollInterval { get; init; } = TimeSpan.FromSeconds(5);
}
