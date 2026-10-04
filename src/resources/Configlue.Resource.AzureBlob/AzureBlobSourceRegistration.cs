using Azure.Core;
using Azure.Storage.Blobs;
using Configlue.Codecs;
using Configlue.Sources;

namespace Configlue.Resource.AzureBlob;

/// <summary>Options for registering a source backed by one Azure Blob Storage blob.</summary>
public sealed class AzureBlobSourceOptions
{
    /// <summary>An optional stable logical source ID for provenance and explicit routing.</summary>
    public string? Id { get; init; }

    /// <summary>The container name.</summary>
    public required string ContainerName { get; init; }

    /// <summary>The blob name.</summary>
    public required string BlobName { get; init; }

    /// <summary>A directly supplied, caller-owned blob client. It remains caller-owned.</summary>
    public BlobClient? Client { get; init; }

    /// <summary>Resolves a caller-owned blob client at context creation.</summary>
    public Func<IServiceProvider?, BlobClient>? ClientFactory { get; init; }

    /// <summary>
    /// A storage account blob-service endpoint used with <see cref="Credential"/>, for example
    /// with <c>DefaultAzureCredential</c>. The value is never written to diagnostics or exceptions.
    /// </summary>
    public Uri? ServiceEndpoint { get; init; }

    /// <summary>
    /// The credential used with <see cref="ServiceEndpoint"/>. The value is never written to
    /// diagnostics or exceptions.
    /// </summary>
    public TokenCredential? Credential { get; init; }

    /// <summary>
    /// A storage connection string. The value is never written to diagnostics or exceptions.
    /// </summary>
    public string? ConnectionString { get; init; }

    /// <summary>
    /// A blob URI, optionally embedding a SAS query. The value is never written to diagnostics
    /// or exceptions.
    /// </summary>
    public Uri? BlobUri { get; init; }

    /// <summary>The codec for the serialized blob.</summary>
    public required StateCodecBinding Codec { get; init; }

    /// <summary>Resource identity and watching settings.</summary>
    public AzureBlobResourceOptions? ResourceOptions { get; init; }

    /// <summary>Higher values are read first.</summary>
    public int Priority { get; init; }

    /// <summary>Read statuses that allow lower-priority sources to be tried.</summary>
    public StateFallbackCondition FallbackCondition { get; init; } =
        StateFallbackCondition.NotFound;

    /// <summary>Whether this source exposes a writer.</summary>
    public bool Writable { get; init; } = true;

    /// <summary>Additional context passed to the codec.</summary>
    public StateCodecContext CodecContext { get; init; }
}

/// <summary>Registers facade sources backed by Azure Blob Storage blobs.</summary>
public static class AzureBlobSourceRegistration
{
    /// <summary>
    /// Adds an Azure Blob source. Supplied clients remain externally owned; clients created from
    /// a connection string, service endpoint, or blob URI are owned by the created resource.
    /// </summary>
    public static ConfiglueSourceRegistration FromAzureBlob(
        this ConfiglueSourceSetBuilder sources,
        AzureBlobSourceOptions options
    )
    {
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.ContainerName);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.BlobName);
        ArgumentNullException.ThrowIfNull(options.Codec);

        var configured =
            (options.Client is null ? 0 : 1)
            + (options.ClientFactory is null ? 0 : 1)
            + (options.ConnectionString is null ? 0 : 1)
            + (options.ServiceEndpoint is null ? 0 : 1)
            + (options.BlobUri is null ? 0 : 1);
        if (configured != 1 || (options.ServiceEndpoint is not null && options.Credential is null))
        {
            throw new ArgumentException(
                "Configure exactly one of Client, ClientFactory, ConnectionString, ServiceEndpoint (with Credential), or BlobUri.",
                nameof(options)
            );
        }

        if (options.Id is not null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(options.Id);
        }

        if (
            options.Client is { } suppliedClient
            && (
                !string.Equals(
                    suppliedClient.BlobContainerName,
                    options.ContainerName,
                    StringComparison.Ordinal
                ) || !string.Equals(suppliedClient.Name, options.BlobName, StringComparison.Ordinal)
            )
        )
        {
            throw new ArgumentException(
                "Client targets a different container or blob than ContainerName and BlobName.",
                nameof(options)
            );
        }

        return ((IConfiglueSourceRegistrationSink)sources).Add(
            new AzureBlobSourceDefinition(options)
        );
    }

    private sealed class AzureBlobSourceDefinition(AzureBlobSourceOptions options)
        : IConfiglueSourceDefinition
    {
        public ConfiglueSourceCreation<TFragment> Create<TFragment>(
            ConfiglueSourceCreationContext context
        )
            where TFragment : class, IConfiglueFragment<TFragment>
        {
            ArgumentNullException.ThrowIfNull(context);
            return context.Complete(
                CreateSourceCore<TFragment>(context, context.ModelSchema, context.Services)
            );
        }

        private StateSource<TFragment> CreateSourceCore<TFragment>(
            ConfiglueSourceCreationContext context,
            ConfiglueModelSchema modelSchema,
            IServiceProvider? serviceProvider
        )
            where TFragment : class, IConfiglueFragment<TFragment>
        {
            ArgumentNullException.ThrowIfNull(modelSchema);
            var client = ResolveClient(serviceProvider);
            var resource = new AzureBlobResource(client, options.ResourceOptions);
            context.Own(resource);
            var serialized = new SerializedSource<TFragment>(
                resource,
                options.Codec,
                options.CodecContext,
                writer: options.Writable ? resource : null,
                watcher: options.ResourceOptions?.EnableWatching == true ? resource : null
            );
            var physicalOrigin = $"azureblob:{options.ContainerName}";
            return ConfiglueSourceCompletion.WithDescribedIdentity(
                serialized,
                options.Id,
                string.Join(
                    "\n",
                    options.ContainerName,
                    options.BlobName,
                    options.ResourceOptions?.ContainerNameSelector?.Method.ToString(),
                    options.ResourceOptions?.BlobNameSelector?.Method.ToString(),
                    options.ResourceOptions?.BlobClientSelector?.Method.ToString()
                ),
                options.Priority,
                options.FallbackCondition,
                physicalOrigin,
                options.ResourceOptions?.FixedResourceId
            );
        }

        private BlobClient ResolveClient(IServiceProvider? serviceProvider)
        {
            if (options.Client is { } client)
            {
                return client;
            }

            if (options.ClientFactory is { } factory)
            {
                return factory(serviceProvider)
                    ?? throw new InvalidOperationException(
                        "The blob client factory returned null."
                    );
            }

            if (options.ConnectionString is { } connectionString)
            {
                return new BlobClient(connectionString, options.ContainerName, options.BlobName);
            }

            if (options.ServiceEndpoint is { } endpoint)
            {
                return new BlobServiceClient(endpoint, options.Credential!)
                    .GetBlobContainerClient(options.ContainerName)
                    .GetBlobClient(options.BlobName);
            }

            return new BlobClient(options.BlobUri!);
        }
    }
}
