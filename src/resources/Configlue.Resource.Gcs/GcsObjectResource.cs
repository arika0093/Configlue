using System.Globalization;
using System.IO.Pipelines;
using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Configlue.Internal;
using Configlue.Sources;
using Google;
using Google.Cloud.Storage.V1;
using GcsObject = Google.Apis.Storage.v1.Data.Object;

namespace Configlue.Resource.Gcs;

/// <summary>Reads and writes one byte resource through a Google Cloud Storage object.</summary>
/// <remarks>
/// Object generations are exposed as revisions and drive generation-conditional writes, so a
/// stale generation surfaces as a normal Configlue conflict. Reads preserve generation,
/// metageneration, ETag, updated timestamp, content type, and size for revision and provenance.
/// Pipeline reads stream bodies without buffering whole payloads. Change polling observes object
/// metadata only and never downloads bodies while the generation is unchanged. Supplied storage
/// clients remain caller-owned; disposal only wakes active change waiters. Diagnostics carry
/// bucket and object names only, never credentials or signed URLs.
/// </remarks>
public sealed class GcsObjectResource
    : IResourceReader,
        IPipelineResourceReader,
        IResourceWriter,
        IResourceIdentity,
        ISourceWatcher,
        IDisposable
{
    private readonly IGcsObjectClient _client;
    private readonly GcsObjectResourceOptions _options;
    private readonly Func<ConfiglueResourceContext, IGcsObjectClient>? _clientSelector;
    private readonly WatchShutdown _watchShutdown = new();
    private int _disposed;

    /// <summary>Creates a resource for one object in a GCS bucket.</summary>
    /// <param name="client">The externally owned storage client used for every operation.</param>
    /// <param name="bucketName">The GCS bucket name.</param>
    /// <param name="objectName">The object name within the bucket.</param>
    /// <param name="options">Resource identity, content-type, and polling settings.</param>
    public GcsObjectResource(
        StorageClient client,
        string bucketName,
        string objectName,
        GcsObjectResourceOptions? options = null
    )
        : this(
            new StorageClientGcsObjectClient(client),
            bucketName,
            objectName,
            options,
            options?.ClientSelector is { } selector
                ? context => new StorageClientGcsObjectClient(selector(context))
                : null
        ) { }

    /// <summary>
    /// Creates a resource that authenticates with Application Default Credentials.
    /// </summary>
    /// <param name="bucketName">The GCS bucket name.</param>
    /// <param name="objectName">The object name within the bucket.</param>
    /// <param name="options">Resource identity, content-type, and polling settings.</param>
    public static GcsObjectResource CreateWithApplicationDefaultCredentials(
        string bucketName,
        string objectName,
        GcsObjectResourceOptions? options = null
    ) => new(StorageClient.Create(), bucketName, objectName, options);

    internal GcsObjectResource(
        IGcsObjectClient client,
        string bucketName,
        string objectName,
        GcsObjectResourceOptions? options = null,
        Func<ConfiglueResourceContext, IGcsObjectClient>? clientSelector = null
    )
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentException.ThrowIfNullOrWhiteSpace(bucketName);
        ArgumentException.ThrowIfNullOrWhiteSpace(objectName);

        options ??= new GcsObjectResourceOptions();
        options.Validate();

        _client = client;
        _options = options;
        _clientSelector = clientSelector;
        BucketName = bucketName;
        ObjectName = objectName;
    }

    /// <summary>The GCS bucket name.</summary>
    public string BucketName { get; }

    /// <summary>The object name within the bucket.</summary>
    public string ObjectName { get; }

    /// <inheritdoc />
    public ResourceId GetResourceId(ConfiglueResourceContext context) =>
        _options.FixedResourceId
        ?? CreateResourceId(
            ResolveBucket(context),
            ResolveObjectName(context),
            _clientSelector is null ? null : context.Route.Value
        );

    /// <inheritdoc />
    public bool IsPipelineReadPreferred => true;

    /// <inheritdoc />
    public async ValueTask<PipelineResourceReadResult> ReadPipelineAsync(
        ConfiglueResourceContext context,
        CancellationToken cancellationToken = default
    )
    {
        var bucketName = ResolveBucket(context);
        var objectName = ResolveObjectName(context);
        var result = await GcsObjectClientExtensions
            .GetObjectStreamAsync(GetClient(context), bucketName, objectName, cancellationToken)
            .ConfigureAwait(false);
        if (result is null)
        {
            return PipelineResourceReadResult.NotFound();
        }

        return PipelineResourceReader.FromStream(result.Content, result.Revision, owner: result);
    }

    /// <inheritdoc />
    public async ValueTask<ResourceReadResult> ReadAsync(
        ConfiglueResourceContext context,
        CancellationToken cancellationToken = default
    )
    {
        var bucketName = ResolveBucket(context);
        var objectName = ResolveObjectName(context);
        var result = await GetClient(context)
            .GetObjectAsync(bucketName, objectName, cancellationToken)
            .ConfigureAwait(false);
        if (result is null)
        {
            return ResourceReadResult.NotFound();
        }

        return ResourceReadResult.Success(result.Content, result.Revision);
    }

    /// <inheritdoc />
    public async ValueTask<StateWriteResult> WriteAsync(
        ConfiglueResourceContext context,
        ResourceWriteRequest request,
        CancellationToken cancellationToken = default
    )
    {
        var bucketName = ResolveBucket(context);
        var objectName = ResolveObjectName(context);
        var client = GetClient(context);
        var (expectedGeneration, requireMissing) = ResolveWritePrecondition(
            request.Condition,
            bucketName,
            objectName
        );
        var contentType = ResolveContentType(context);
        if (contentType is null && _options.PreserveContentTypeOnOverwrite && !requireMissing)
        {
            contentType = (
                await client
                    .GetMetadataAsync(bucketName, objectName, cancellationToken)
                    .ConfigureAwait(false)
            )?.ContentType;
        }

        try
        {
            var result = await client
                .PutObjectAsync(
                    bucketName,
                    objectName,
                    request.Content,
                    expectedGeneration,
                    requireMissing,
                    contentType,
                    cancellationToken
                )
                .ConfigureAwait(false);
            return new StateWriteResult(result.Revision);
        }
        catch (GcsObjectConflictException)
        {
            throw new StateConflictException(
                $"The GCS object '{bucketName}/{objectName}' changed after it was read."
            );
        }
    }

    /// <inheritdoc />
    public ValueTask WaitForChangeAsync(
        ConfiglueResourceContext context,
        string? observedRevision,
        CancellationToken cancellationToken = default
    )
    {
        var bucketName = ResolveBucket(context);
        var objectName = ResolveObjectName(context);
        var client = GetClient(context);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        return _watchShutdown.WaitAsync(
            watchCancellationToken =>
                PollUntilChangedAsync(
                    client,
                    bucketName,
                    objectName,
                    observedRevision,
                    watchCancellationToken
                ),
            cancellationToken
        );
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

    private static (long? ExpectedGeneration, bool RequireMissing) ResolveWritePrecondition(
        RevisionCondition condition,
        string bucketName,
        string objectName
    )
    {
        if (condition.IsNone)
        {
            return (null, false);
        }

        if (condition.IsMustNotExist)
        {
            return (null, true);
        }

        if (
            condition.Revision is not null
            && long.TryParse(
                condition.Revision,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var generation
            )
        )
        {
            return (generation, false);
        }

        throw new StateConflictException(
            $"The GCS object '{bucketName}/{objectName}' changed after it was read."
        );
    }

    private async ValueTask PollUntilChangedAsync(
        IGcsObjectClient client,
        string bucketName,
        string objectName,
        string? observedRevision,
        CancellationToken cancellationToken
    )
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var metadata = await client
                .GetMetadataAsync(bucketName, objectName, cancellationToken)
                .ConfigureAwait(false);
            if (
                !string.Equals(
                    FormatRevision(metadata?.Generation),
                    observedRevision,
                    StringComparison.Ordinal
                )
            )
            {
                return;
            }

            await Task.Delay(_options.PollInterval, cancellationToken).ConfigureAwait(false);
        }
    }

    private IGcsObjectClient GetClient(ConfiglueResourceContext context) =>
        _clientSelector?.Invoke(context) ?? _client;

    private string ResolveBucket(ConfiglueResourceContext context)
    {
        var bucketName = _options.BucketNameSelector?.Invoke(context) ?? BucketName;
        ArgumentException.ThrowIfNullOrWhiteSpace(bucketName);
        return bucketName;
    }

    private string ResolveObjectName(ConfiglueResourceContext context)
    {
        var objectName = _options.ObjectNameSelector?.Invoke(context) ?? ObjectName;
        ArgumentException.ThrowIfNullOrWhiteSpace(objectName);
        return objectName;
    }

    private string? ResolveContentType(ConfiglueResourceContext context)
    {
        var contentType = _options.ContentTypeSelector?.Invoke(context) ?? _options.ContentType;
        if (contentType is not null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(contentType);
        }

        return contentType;
    }

    private static string? FormatRevision(long? generation) =>
        generation?.ToString(CultureInfo.InvariantCulture);

    private static ResourceId CreateResourceId(
        string bucketName,
        string objectName,
        string? route = null
    )
    {
        var identity = Encoding.UTF8.GetBytes(
            route is null
                ? bucketName + "\n" + objectName
                : bucketName + "\n" + objectName + "\n" + route
        );
        return new ResourceId(
            $"gcs:{Convert.ToHexString(SHA256.HashData(identity)).ToLowerInvariant()}"
        );
    }

    private sealed class StorageClientGcsObjectClient : IGcsObjectClient
    {
        private const string DefaultContentType = "application/octet-stream";

        private readonly StorageClient _client;

        public StorageClientGcsObjectClient(StorageClient client)
        {
            ArgumentNullException.ThrowIfNull(client);
            _client = client;
        }

        public async Task<GcsObjectReadResult?> GetObjectAsync(
            string bucketName,
            string objectName,
            CancellationToken cancellationToken
        )
        {
            using var buffer = new MemoryStream();
            GcsObject downloaded;
            try
            {
                downloaded = await _client
                    .DownloadObjectAsync(bucketName, objectName, buffer, null, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (GoogleApiException exception) when (IsNotFound(exception))
            {
                return null;
            }

            var metadata = GcsObjectMetadataMapper.FromObject(downloaded);
            return new GcsObjectReadResult(
                buffer.ToArray(),
                FormatRevision(metadata.Generation),
                metadata
            );
        }

        public async Task<GcsObjectMetadata?> GetMetadataAsync(
            string bucketName,
            string objectName,
            CancellationToken cancellationToken
        )
        {
            try
            {
                var metadata = await _client
                    .GetObjectAsync(bucketName, objectName, null, cancellationToken)
                    .ConfigureAwait(false);
                return GcsObjectMetadataMapper.FromObject(metadata);
            }
            catch (GoogleApiException exception) when (IsNotFound(exception))
            {
                return null;
            }
        }

        public async Task DownloadObjectAsync(
            string bucketName,
            string objectName,
            Stream destination,
            CancellationToken cancellationToken
        )
        {
            try
            {
                await _client
                    .DownloadObjectAsync(
                        bucketName,
                        objectName,
                        destination,
                        null,
                        cancellationToken
                    )
                    .ConfigureAwait(false);
            }
            catch (GoogleApiException exception) when (IsNotFound(exception))
            {
                throw new GcsObjectMissingException(bucketName, objectName, exception);
            }
        }

        public async Task<GcsObjectWriteResult> PutObjectAsync(
            string bucketName,
            string objectName,
            ReadOnlyMemory<byte> content,
            long? expectedGeneration,
            bool requireMissing,
            string? contentType,
            CancellationToken cancellationToken
        )
        {
            using var input = CreateReadOnlyStream(content);
            long? ifGenerationMatch = null;
            if (requireMissing)
            {
                ifGenerationMatch = 0;
            }
            else if (expectedGeneration.HasValue)
            {
                ifGenerationMatch = expectedGeneration;
            }

            UploadObjectOptions? options = ifGenerationMatch.HasValue
                ? new UploadObjectOptions { IfGenerationMatch = ifGenerationMatch }
                : null;
            GcsObject uploaded;
            try
            {
                uploaded = await _client
                    .UploadObjectAsync(
                        bucketName,
                        objectName,
                        contentType ?? DefaultContentType,
                        input,
                        options,
                        cancellationToken
                    )
                    .ConfigureAwait(false);
            }
            catch (GoogleApiException exception) when (IsPreconditionFailure(exception))
            {
                throw new GcsObjectConflictException(bucketName, objectName, exception);
            }

            var metadata = GcsObjectMetadataMapper.FromObject(uploaded);
            return new GcsObjectWriteResult(FormatRevision(metadata.Generation), metadata);
        }

        private static MemoryStream CreateReadOnlyStream(ReadOnlyMemory<byte> content)
        {
            if (MemoryMarshal.TryGetArray(content, out var segment) && segment.Array is not null)
            {
                return new MemoryStream(
                    segment.Array,
                    segment.Offset,
                    segment.Count,
                    writable: false
                );
            }

            return new MemoryStream(content.ToArray(), writable: false);
        }

        private static bool IsNotFound(GoogleApiException exception) =>
            exception.HttpStatusCode == HttpStatusCode.NotFound;

        private static bool IsPreconditionFailure(GoogleApiException exception) =>
            exception.HttpStatusCode == HttpStatusCode.PreconditionFailed;
    }
}

internal static class GcsObjectMetadataMapper
{
    public static GcsObjectMetadata FromObject(GcsObject metadata)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        // UpdatedDateTimeOffset is the supported timestamp; the legacy Updated DateTime is
        // obsolete and intentionally ignored here.
        DateTimeOffset? updated = metadata.UpdatedDateTimeOffset;
        long? size =
            metadata.Size.HasValue && metadata.Size.Value <= long.MaxValue
                ? (long)metadata.Size.Value
                : null;
        return new GcsObjectMetadata(
            metadata.Generation,
            metadata.Metageneration,
            metadata.ETag,
            updated,
            metadata.ContentType,
            size
        );
    }
}

internal interface IGcsObjectClient
{
    Task<GcsObjectReadResult?> GetObjectAsync(
        string bucketName,
        string objectName,
        CancellationToken cancellationToken
    );

    Task<GcsObjectMetadata?> GetMetadataAsync(
        string bucketName,
        string objectName,
        CancellationToken cancellationToken
    );

    Task DownloadObjectAsync(
        string bucketName,
        string objectName,
        Stream destination,
        CancellationToken cancellationToken
    );

    Task<GcsObjectWriteResult> PutObjectAsync(
        string bucketName,
        string objectName,
        ReadOnlyMemory<byte> content,
        long? expectedGeneration,
        bool requireMissing,
        string? contentType,
        CancellationToken cancellationToken
    );
}

internal static class GcsObjectClientExtensions
{
    public static async Task<GcsObjectStreamResult?> GetObjectStreamAsync(
        IGcsObjectClient client,
        string bucketName,
        string objectName,
        CancellationToken cancellationToken
    )
    {
        var metadata = await client
            .GetMetadataAsync(bucketName, objectName, cancellationToken)
            .ConfigureAwait(false);
        if (metadata is null)
        {
            return null;
        }

        // Stream the body through a pipe so pipeline consumers observe bytes without an
        // intermediate full-payload copy. The background download only forwards the body;
        // the revision comes from the metadata check above.
        var pipe = new Pipe();
        var downloadCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken
        );
        // The fill task is self-observing: it captures every failure into the pipe, so no
        // await is required for exception propagation or disposal.
        _ = FillPipeAsync(client, bucketName, objectName, pipe.Writer, downloadCancellation.Token);
        var content = pipe.Reader.AsStream();
        return new GcsObjectStreamResult(
            content,
            metadata.Generation?.ToString(CultureInfo.InvariantCulture),
            metadata,
            downloadCancellation
        );
    }

    private static async Task FillPipeAsync(
        IGcsObjectClient client,
        string bucketName,
        string objectName,
        PipeWriter writer,
        CancellationToken cancellationToken
    )
    {
        // The download task never faults: failures complete the pipe so readers observe
        // them, which keeps disposal fire-and-forget without unobserved exceptions.
        try
        {
            using var destination = writer.AsStream(leaveOpen: true);
            await client
                .DownloadObjectAsync(bucketName, objectName, destination, cancellationToken)
                .ConfigureAwait(false);
            await writer.CompleteAsync().ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            await writer.CompleteAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            await writer.CompleteAsync(exception).ConfigureAwait(false);
        }
    }
}

internal sealed class GcsObjectStreamResult : IDisposable
{
    private readonly CancellationTokenSource _downloadCancellation;
    private int _disposed;

    public GcsObjectStreamResult(
        Stream content,
        string? revision,
        GcsObjectMetadata metadata,
        CancellationTokenSource downloadCancellation
    )
    {
        Content = content;
        Revision = revision;
        Metadata = metadata;
        _downloadCancellation = downloadCancellation;
    }

    public Stream Content { get; }

    public string? Revision { get; }

    public GcsObjectMetadata Metadata { get; }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        try
        {
            Content.Dispose();
        }
        finally
        {
            if (!_downloadCancellation.IsCancellationRequested)
            {
                _downloadCancellation.Cancel();
            }

            _downloadCancellation.Dispose();
        }
    }
}

internal sealed record GcsObjectReadResult(
    ReadOnlyMemory<byte> Content,
    string? Revision,
    GcsObjectMetadata Metadata
);

internal sealed record GcsObjectWriteResult(string? Revision, GcsObjectMetadata Metadata);

/// <summary>Signals that a GCS object was missing when its body was requested.</summary>
public sealed class GcsObjectMissingException : Exception
{
    /// <summary>Creates a missing-object signal.</summary>
    public GcsObjectMissingException(
        string bucketName,
        string objectName,
        Exception? innerException = null
    )
        : base($"The GCS object '{bucketName}/{objectName}' was not found.", innerException)
    {
        BucketName = bucketName;
        ObjectName = objectName;
    }

    /// <summary>The bucket that was addressed.</summary>
    public string BucketName { get; }

    /// <summary>The object name that was addressed.</summary>
    public string ObjectName { get; }
}

/// <summary>Signals that a GCS generation precondition failed.</summary>
public sealed class GcsObjectConflictException : Exception
{
    /// <summary>Creates a generation-conflict signal.</summary>
    public GcsObjectConflictException(
        string bucketName,
        string objectName,
        Exception? innerException = null
    )
        : base(
            $"The GCS object '{bucketName}/{objectName}' failed a generation precondition.",
            innerException
        )
    {
        BucketName = bucketName;
        ObjectName = objectName;
    }

    /// <summary>The bucket that was addressed.</summary>
    public string BucketName { get; }

    /// <summary>The object name that was addressed.</summary>
    public string ObjectName { get; }
}
