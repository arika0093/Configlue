using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Configlue.Internal;
using Configlue.Sources;

namespace Configlue.Resource.AzureBlob;

/// <summary>Reads and writes one byte resource through an Azure Blob Storage blob.</summary>
/// <remarks>
/// Blob ETags are exposed as revisions and used for conditional writes. Downloads stream the
/// payload without intermediate buffering, while change observation polls blob properties
/// (ETag metadata) and never downloads the payload merely to detect changes.
/// <para>
/// Authentication material such as connection strings or SAS query values is never included in
/// exception messages, resource identities, or physical-origin diagnostics.
/// </para>
/// </remarks>
public sealed class AzureBlobResource
    : IResourceReader,
        IPipelineResourceReader,
        IResourceWriter,
        IResourceIdentity,
        ISourceWatcher,
        IDisposable
{
    private readonly IAzureBlobClient _client;
    private readonly AzureBlobResourceOptions _options;
    private readonly Func<ConfiglueResourceContext, AzureBlobBinding>? _bindingSelector;
    private readonly string _containerName;
    private readonly string _blobName;
    private readonly WatchShutdown _watchShutdown = new();
    private int _disposed;

    /// <summary>Creates a resource for one blob resolved through a service client.</summary>
    public AzureBlobResource(
        BlobServiceClient serviceClient,
        string containerName,
        string blobName,
        AzureBlobResourceOptions? options = null
    )
        : this(
            new BlobServiceClientAdapter(serviceClient),
            containerName,
            blobName,
            options,
            MapSelector(options)
        ) { }

    /// <summary>Creates a resource for one blob inside an injected container client.</summary>
    public AzureBlobResource(
        BlobContainerClient containerClient,
        string blobName,
        AzureBlobResourceOptions? options = null
    )
        : this(
            new BlobContainerClientAdapter(containerClient),
            containerClient?.Name ?? throw new ArgumentNullException(nameof(containerClient)),
            blobName,
            options,
            MapSelector(options)
        ) { }

    /// <summary>Creates a resource for one blob through an injected, fully bound blob client.</summary>
    public AzureBlobResource(BlobClient blobClient, AzureBlobResourceOptions? options = null)
        : this(
            blobClient is null
                ? throw new ArgumentNullException(nameof(blobClient))
                : new SingleBlobClientAdapter(blobClient),
            blobClient?.BlobContainerName ?? throw new ArgumentNullException(nameof(blobClient)),
            blobClient?.Name ?? throw new ArgumentNullException(nameof(blobClient)),
            options,
            MapSelector(options)
        ) { }

    internal AzureBlobResource(
        IAzureBlobClient client,
        string containerName,
        string blobName,
        AzureBlobResourceOptions? options = null,
        Func<ConfiglueResourceContext, AzureBlobBinding>? bindingSelector = null
    )
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentException.ThrowIfNullOrWhiteSpace(containerName);
        ArgumentException.ThrowIfNullOrWhiteSpace(blobName);

        _client = client;
        _options = options ?? new AzureBlobResourceOptions();
        if (
            _options.BlobClientSelector is not null
            && (_options.ContainerNameSelector is not null || _options.BlobNameSelector is not null)
        )
        {
            throw new ArgumentException(
                "BlobClientSelector cannot be combined with ContainerNameSelector or BlobNameSelector because the selected client already determines the container and blob.",
                nameof(options)
            );
        }

        if (
            _options.WatchPollInterval <= TimeSpan.Zero
            || _options.WatchPollInterval.TotalMilliseconds > int.MaxValue
        )
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "WatchPollInterval must be greater than zero and at most Int32.MaxValue milliseconds."
            );
        }

        _bindingSelector = bindingSelector;
        _containerName = containerName;
        _blobName = blobName;
    }

    /// <summary>The default container name.</summary>
    public string ContainerName => _containerName;

    /// <summary>The default blob name.</summary>
    public string BlobName => _blobName;

    /// <inheritdoc />
    public ResourceId GetResourceId(ConfiglueResourceContext context) =>
        _options.FixedResourceId ?? CreateResourceId(ResolveBinding(context));

    /// <inheritdoc />
    public bool IsPipelineReadPreferred => true;

    /// <inheritdoc />
    public async ValueTask<PipelineResourceReadResult> ReadPipelineAsync(
        ConfiglueResourceContext context,
        CancellationToken cancellationToken = default
    )
    {
        ThrowIfDisposed();
        var binding = ResolveBinding(context);
        try
        {
            var result = await AzureBlobClientExtensions
                .DownloadStreamingAsync(
                    binding.Client,
                    binding.ContainerName,
                    binding.BlobName,
                    cancellationToken
                )
                .ConfigureAwait(false);
            return PipelineResourceReader.FromStream(result.Content, result.ETag);
        }
        catch (RequestFailedException exception) when (IsMissingBlob(exception))
        {
            return PipelineResourceReadResult.NotFound();
        }
    }

    /// <inheritdoc />
    public async ValueTask<ResourceReadResult> ReadAsync(
        ConfiglueResourceContext context,
        CancellationToken cancellationToken = default
    )
    {
        ThrowIfDisposed();
        var binding = ResolveBinding(context);
        try
        {
            var result = await binding
                .Client.DownloadAsync(binding.ContainerName, binding.BlobName, cancellationToken)
                .ConfigureAwait(false);
            return ResourceReadResult.Success(result.Content, result.ETag);
        }
        catch (RequestFailedException exception) when (IsMissingBlob(exception))
        {
            return ResourceReadResult.NotFound();
        }
    }

    /// <inheritdoc />
    public async ValueTask<StateWriteResult> WriteAsync(
        ConfiglueResourceContext context,
        ResourceWriteRequest request,
        CancellationToken cancellationToken = default
    )
    {
        ThrowIfDisposed();
        var binding = ResolveBinding(context);
        var checkRevision = !request.Condition.IsNone;
        try
        {
            var result = await binding
                .Client.UploadAsync(
                    binding.ContainerName,
                    binding.BlobName,
                    request.Content,
                    checkRevision ? request.Condition.Revision : null,
                    checkRevision && request.Condition.Revision is null,
                    _options.ContentType,
                    _options.Metadata,
                    cancellationToken
                )
                .ConfigureAwait(false);
            return new StateWriteResult(result.ETag);
        }
        catch (RequestFailedException exception)
            when (checkRevision && IsRevisionConflict(exception))
        {
            throw new StateConflictException(
                $"The blob '{binding.ContainerName}/{binding.BlobName}' changed after it was read."
            );
        }
    }

    /// <inheritdoc />
    /// <inheritdoc />
    /// <remarks>
    /// Polls blob properties (ETag metadata) through the shared polling primitive and
    /// never downloads the payload merely to detect changes.
    /// </remarks>
    public async ValueTask WaitForChangeAsync(
        ConfiglueResourceContext context,
        string? observedRevision,
        CancellationToken cancellationToken = default
    )
    {
        ThrowIfDisposed();
        if (!_options.EnableWatching)
        {
            throw new InvalidOperationException(
                "Azure Blob change watching is not enabled. Set AzureBlobResourceOptions.EnableWatching to receive change notifications."
            );
        }

        var binding = ResolveBinding(context);
        await _watchShutdown
            .WaitAsync(
                watchCancellationToken =>
                    PollingWatch.WaitForRevisionChangeAsync(
                        cancellation => new ValueTask<string?>(
                            GetCurrentRevisionAsync(binding, cancellation)
                        ),
                        observedRevision,
                        _options.WatchPollInterval,
                        watchCancellationToken
                    ),
                cancellationToken
            )
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _watchShutdown.Signal();
    }

    private AzureBlobBinding ResolveBinding(ConfiglueResourceContext context)
    {
        if (_bindingSelector is { } selector)
        {
            var binding =
                selector(context)
                ?? throw new InvalidOperationException("The blob binding selector returned null.");
            if (binding.Client is null)
            {
                throw new InvalidOperationException("The blob binding selector returned null.");
            }

            ArgumentException.ThrowIfNullOrWhiteSpace(binding.ContainerName);
            ArgumentException.ThrowIfNullOrWhiteSpace(binding.BlobName);
            return binding;
        }

        var containerName = _options.ContainerNameSelector?.Invoke(context) ?? _containerName;
        ArgumentException.ThrowIfNullOrWhiteSpace(containerName);
        var blobName = _options.BlobNameSelector?.Invoke(context) ?? _blobName;
        ArgumentException.ThrowIfNullOrWhiteSpace(blobName);
        return new AzureBlobBinding(_client, containerName, blobName);
    }

    private void ThrowIfDisposed() =>
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

    private static async Task<string?> GetCurrentRevisionAsync(
        AzureBlobBinding binding,
        CancellationToken cancellationToken
    )
    {
        try
        {
            var properties = await binding
                .Client.GetPropertiesAsync(
                    binding.ContainerName,
                    binding.BlobName,
                    cancellationToken
                )
                .ConfigureAwait(false);
            return properties?.ETag;
        }
        catch (RequestFailedException exception) when (IsMissingBlob(exception))
        {
            return null;
        }
    }

    private static bool IsMissingBlob(RequestFailedException exception) =>
        exception.ErrorCode is "BlobNotFound" or "NotFound"
        || (exception.Status == 404 && string.IsNullOrEmpty(exception.ErrorCode));

    private static bool IsRevisionConflict(RequestFailedException exception) =>
        exception.Status is 409 or 412
        || exception.ErrorCode
            is "ConditionNotMet"
                or "PreconditionFailed"
                or "ConditionalRequestConflict"
                or "BlobAlreadyExists";

    private static ResourceId CreateResourceId(AzureBlobBinding binding)
    {
        var identity = Encoding.UTF8.GetBytes(
            binding.Route is null
                ? binding.ContainerName + "\n" + binding.BlobName
                : binding.ContainerName + "\n" + binding.BlobName + "\n" + binding.Route
        );
        return new ResourceId(
            $"azureblob:{Convert.ToHexString(SHA256.HashData(identity)).ToLowerInvariant()}"
        );
    }

    private static Func<ConfiglueResourceContext, AzureBlobBinding>? MapSelector(
        AzureBlobResourceOptions? options
    )
    {
        if (options?.BlobClientSelector is not { } selector)
        {
            return null;
        }

        return context =>
        {
            var client =
                selector(context)
                ?? throw new InvalidOperationException("The blob client selector returned null.");
            return new AzureBlobBinding(
                new SingleBlobClientAdapter(client),
                client.BlobContainerName,
                client.Name,
                context.Route.Value
            );
        };
    }

    private sealed class BlobServiceClientAdapter : IAzureBlobClient, IAzureBlobStreamClient
    {
        private readonly BlobServiceClient _serviceClient;

        public BlobServiceClientAdapter(BlobServiceClient serviceClient)
        {
            ArgumentNullException.ThrowIfNull(serviceClient);
            _serviceClient = serviceClient;
        }

        public Task<AzureBlobReadResult> DownloadAsync(
            string containerName,
            string blobName,
            CancellationToken cancellationToken
        ) => DownloadCoreAsync(Resolve(containerName, blobName), cancellationToken);

        public async Task<AzureBlobStreamResult> DownloadStreamingAsync(
            string containerName,
            string blobName,
            CancellationToken cancellationToken
        )
        {
            var client = Resolve(containerName, blobName);
            var response = await client
                .DownloadStreamingAsync(cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            return new AzureBlobStreamResult(
                response.Value.Content,
                response.Value.Details.ETag.ToString()
            );
        }

        public async Task<AzureBlobPropertiesResult?> GetPropertiesAsync(
            string containerName,
            string blobName,
            CancellationToken cancellationToken
        )
        {
            var response = await Resolve(containerName, blobName)
                .GetPropertiesAsync(cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            var properties = response.Value;
            return new AzureBlobPropertiesResult(
                properties.ETag.ToString(),
                properties.LastModified,
                properties.ContentLength,
                properties.ContentType,
                properties.VersionId
            );
        }

        public async Task<AzureBlobWriteResult> UploadAsync(
            string containerName,
            string blobName,
            ReadOnlyMemory<byte> content,
            string? expectedETag,
            bool requireMissing,
            string? contentType,
            IDictionary<string, string>? metadata,
            CancellationToken cancellationToken
        )
        {
            using var input = CreateUploadStream(content);
            var response = await Resolve(containerName, blobName)
                .UploadAsync(
                    input,
                    CreateUploadOptions(expectedETag, requireMissing, contentType, metadata),
                    cancellationToken
                )
                .ConfigureAwait(false);
            return UploadResult(response.Value);
        }

        private BlobClient Resolve(string containerName, string blobName) =>
            _serviceClient.GetBlobContainerClient(containerName).GetBlobClient(blobName);
    }

    private sealed class BlobContainerClientAdapter : IAzureBlobClient, IAzureBlobStreamClient
    {
        private readonly BlobContainerClient _containerClient;

        public BlobContainerClientAdapter(BlobContainerClient containerClient)
        {
            ArgumentNullException.ThrowIfNull(containerClient);
            _containerClient = containerClient;
        }

        public Task<AzureBlobReadResult> DownloadAsync(
            string containerName,
            string blobName,
            CancellationToken cancellationToken
        ) => DownloadCoreAsync(Resolve(containerName, blobName), cancellationToken);

        public async Task<AzureBlobStreamResult> DownloadStreamingAsync(
            string containerName,
            string blobName,
            CancellationToken cancellationToken
        )
        {
            var client = Resolve(containerName, blobName);
            var response = await client
                .DownloadStreamingAsync(cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            return new AzureBlobStreamResult(
                response.Value.Content,
                response.Value.Details.ETag.ToString()
            );
        }

        public async Task<AzureBlobPropertiesResult?> GetPropertiesAsync(
            string containerName,
            string blobName,
            CancellationToken cancellationToken
        )
        {
            var response = await Resolve(containerName, blobName)
                .GetPropertiesAsync(cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            var properties = response.Value;
            return new AzureBlobPropertiesResult(
                properties.ETag.ToString(),
                properties.LastModified,
                properties.ContentLength,
                properties.ContentType,
                properties.VersionId
            );
        }

        public async Task<AzureBlobWriteResult> UploadAsync(
            string containerName,
            string blobName,
            ReadOnlyMemory<byte> content,
            string? expectedETag,
            bool requireMissing,
            string? contentType,
            IDictionary<string, string>? metadata,
            CancellationToken cancellationToken
        )
        {
            using var input = CreateUploadStream(content);
            var response = await Resolve(containerName, blobName)
                .UploadAsync(
                    input,
                    CreateUploadOptions(expectedETag, requireMissing, contentType, metadata),
                    cancellationToken
                )
                .ConfigureAwait(false);
            return UploadResult(response.Value);
        }

        private BlobClient Resolve(string containerName, string blobName)
        {
            if (!string.Equals(containerName, _containerClient.Name, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"The injected container client cannot address container '{containerName}'. Use a BlobServiceClient-based resource to address multiple containers."
                );
            }

            return _containerClient.GetBlobClient(blobName);
        }
    }

    private sealed class SingleBlobClientAdapter : IAzureBlobClient, IAzureBlobStreamClient
    {
        private readonly BlobClient _blobClient;

        public SingleBlobClientAdapter(BlobClient blobClient)
        {
            ArgumentNullException.ThrowIfNull(blobClient);
            _blobClient = blobClient;
        }

        public Task<AzureBlobReadResult> DownloadAsync(
            string containerName,
            string blobName,
            CancellationToken cancellationToken
        ) => DownloadCoreAsync(Resolve(containerName, blobName), cancellationToken);

        public async Task<AzureBlobStreamResult> DownloadStreamingAsync(
            string containerName,
            string blobName,
            CancellationToken cancellationToken
        )
        {
            var client = Resolve(containerName, blobName);
            var response = await client
                .DownloadStreamingAsync(cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            return new AzureBlobStreamResult(
                response.Value.Content,
                response.Value.Details.ETag.ToString()
            );
        }

        public async Task<AzureBlobPropertiesResult?> GetPropertiesAsync(
            string containerName,
            string blobName,
            CancellationToken cancellationToken
        )
        {
            var response = await Resolve(containerName, blobName)
                .GetPropertiesAsync(cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            var properties = response.Value;
            return new AzureBlobPropertiesResult(
                properties.ETag.ToString(),
                properties.LastModified,
                properties.ContentLength,
                properties.ContentType,
                properties.VersionId
            );
        }

        public async Task<AzureBlobWriteResult> UploadAsync(
            string containerName,
            string blobName,
            ReadOnlyMemory<byte> content,
            string? expectedETag,
            bool requireMissing,
            string? contentType,
            IDictionary<string, string>? metadata,
            CancellationToken cancellationToken
        )
        {
            using var input = CreateUploadStream(content);
            var response = await Resolve(containerName, blobName)
                .UploadAsync(
                    input,
                    CreateUploadOptions(expectedETag, requireMissing, contentType, metadata),
                    cancellationToken
                )
                .ConfigureAwait(false);
            return UploadResult(response.Value);
        }

        private BlobClient Resolve(string containerName, string blobName)
        {
            if (
                !string.Equals(
                    containerName,
                    _blobClient.BlobContainerName,
                    StringComparison.Ordinal
                ) || !string.Equals(blobName, _blobClient.Name, StringComparison.Ordinal)
            )
            {
                throw new InvalidOperationException(
                    $"The injected blob client cannot address blob '{containerName}/{blobName}'. Use a service- or container-based resource to address multiple blobs."
                );
            }

            return _blobClient;
        }
    }

    private static async Task<AzureBlobReadResult> DownloadCoreAsync(
        BlobClient client,
        CancellationToken cancellationToken
    )
    {
        var response = await client
            .DownloadContentAsync(cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        var result = response.Value;
        var details = result.Details;
        return new AzureBlobReadResult(
            result.Content.ToArray(),
            details.ETag.ToString(),
            details.LastModified,
            details.ContentLength,
            details.ContentType,
            details.VersionId
        );
    }

    private static BlobUploadOptions CreateUploadOptions(
        string? expectedETag,
        bool requireMissing,
        string? contentType,
        IDictionary<string, string>? metadata
    )
    {
        BlobRequestConditions? conditions = null;
        if (expectedETag is not null)
        {
            conditions = new BlobRequestConditions { IfMatch = new ETag(expectedETag) };
        }
        else if (requireMissing)
        {
            conditions = new BlobRequestConditions { IfNoneMatch = ETag.All };
        }

        BlobHttpHeaders? headers = null;
        if (contentType is not null)
        {
            headers = new BlobHttpHeaders { ContentType = contentType };
        }

        return new BlobUploadOptions
        {
            Conditions = conditions,
            HttpHeaders = headers,
            Metadata = metadata is null ? null : new Dictionary<string, string>(metadata),
        };
    }

    private static AzureBlobWriteResult UploadResult(BlobContentInfo info) =>
        new(info.ETag.ToString(), info.LastModified, info.VersionId);

    private static MemoryStream CreateUploadStream(ReadOnlyMemory<byte> content)
    {
        if (MemoryMarshal.TryGetArray(content, out var segment) && segment.Array is not null)
        {
            return new MemoryStream(segment.Array, segment.Offset, segment.Count, writable: false);
        }

        return new MemoryStream(content.ToArray(), writable: false);
    }
}

internal sealed record AzureBlobBinding(
    IAzureBlobClient Client,
    string ContainerName,
    string BlobName,
    string? Route = null
);

internal interface IAzureBlobClient
{
    Task<AzureBlobReadResult> DownloadAsync(
        string containerName,
        string blobName,
        CancellationToken cancellationToken
    );

    Task<AzureBlobPropertiesResult?> GetPropertiesAsync(
        string containerName,
        string blobName,
        CancellationToken cancellationToken
    );

    Task<AzureBlobWriteResult> UploadAsync(
        string containerName,
        string blobName,
        ReadOnlyMemory<byte> content,
        string? expectedETag,
        bool requireMissing,
        string? contentType,
        IDictionary<string, string>? metadata,
        CancellationToken cancellationToken
    );
}

internal interface IAzureBlobStreamClient
{
    Task<AzureBlobStreamResult> DownloadStreamingAsync(
        string containerName,
        string blobName,
        CancellationToken cancellationToken
    );
}

internal static class AzureBlobClientExtensions
{
    public static async Task<AzureBlobStreamResult> DownloadStreamingAsync(
        IAzureBlobClient client,
        string containerName,
        string blobName,
        CancellationToken cancellationToken
    )
    {
        if (client is IAzureBlobStreamClient streamClient)
        {
            return await streamClient
                .DownloadStreamingAsync(containerName, blobName, cancellationToken)
                .ConfigureAwait(false);
        }

        var result = await client
            .DownloadAsync(containerName, blobName, cancellationToken)
            .ConfigureAwait(false);
        var content =
            MemoryMarshal.TryGetArray(result.Content, out var segment) && segment.Array is not null
                ? new MemoryStream(segment.Array, segment.Offset, segment.Count, writable: false)
                : new MemoryStream(result.Content.ToArray(), writable: false);
        return new AzureBlobStreamResult(content, result.ETag);
    }
}

internal sealed class AzureBlobStreamResult : IDisposable
{
    private readonly IDisposable? _owner;

    public AzureBlobStreamResult(Stream content, string? eTag, IDisposable? owner = null)
    {
        Content = content;
        ETag = eTag;
        _owner = owner;
    }

    public Stream Content { get; }

    public string? ETag { get; }

    public void Dispose()
    {
        if (_owner is not null)
        {
            _owner.Dispose();
        }
        else
        {
            Content.Dispose();
        }
    }
}

internal sealed record AzureBlobReadResult(
    ReadOnlyMemory<byte> Content,
    string? ETag,
    DateTimeOffset LastModified,
    long ContentLength,
    string? ContentType,
    string? VersionId
);

internal sealed record AzureBlobPropertiesResult(
    string? ETag,
    DateTimeOffset LastModified,
    long ContentLength,
    string? ContentType,
    string? VersionId
);

internal sealed record AzureBlobWriteResult(
    string? ETag,
    DateTimeOffset LastModified,
    string? VersionId
);
