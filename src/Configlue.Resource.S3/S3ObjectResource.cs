using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Amazon.S3;
using Amazon.S3.Model;

namespace Configlue.Resource.S3;

/// <summary>Reads and writes one byte resource through an Amazon S3 object.</summary>
/// <remarks>
/// Object ETags are exposed as revisions and used for conditional writes. S3-compatible
/// implementations may not provide the same ETag or conditional-request guarantees.
/// </remarks>
public sealed class S3ObjectResource
    : IContextualResourceReader,
        IContextualPipelineResourceReader,
        IContextualResourceWriter,
        IContextualResourceIdentity
{
    private readonly IS3ObjectClient _client;
    private readonly S3ObjectResourceOptions _options;
    private readonly Func<ConfiglueResourceContext, IS3ObjectClient>? _clientSelector;

    /// <summary>Creates a resource for one object in an S3 bucket.</summary>
    public S3ObjectResource(
        IAmazonS3 client,
        string bucketName,
        string key,
        S3ObjectResourceOptions? options = null
    )
        : this(
            new S3ObjectClient(client),
            bucketName,
            key,
            options,
            options?.ClientSelector is { } selector
                ? context => new S3ObjectClient(selector(context))
                : null
        ) { }

    internal S3ObjectResource(
        IS3ObjectClient client,
        string bucketName,
        string key,
        S3ObjectResourceOptions? options = null,
        Func<ConfiglueResourceContext, IS3ObjectClient>? clientSelector = null
    )
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentException.ThrowIfNullOrWhiteSpace(bucketName);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        _client = client;
        _options = options ?? new S3ObjectResourceOptions();
        _clientSelector = clientSelector;
        BucketName = bucketName;
        Key = key;
        ResourceId = _options.ResourceId ?? GetResourceId(ConfiglueResourceContext.Default);
    }

    /// <summary>The S3 bucket name.</summary>
    public string BucketName { get; }

    /// <summary>The object key.</summary>
    public string Key { get; }

    /// <inheritdoc />
    public ResourceId ResourceId { get; }

    /// <inheritdoc />
    public ResourceId GetResourceId(ConfiglueResourceContext context) =>
        _options.ResourceId
        ?? CreateResourceId(
            ResolveBucket(context),
            ResolveKey(context),
            _clientSelector is null ? null : context.Route.Value
        );

    /// <inheritdoc />
    public bool IsPipelineReadPreferred => true;

    /// <inheritdoc />
    public async ValueTask<PipelineResourceReadResult> ReadPipelineAsync(
        CancellationToken cancellationToken = default
    ) =>
        await ReadPipelineAsync(ConfiglueResourceContext.Default, cancellationToken)
            .ConfigureAwait(false);

    /// <inheritdoc />
    public async ValueTask<PipelineResourceReadResult> ReadPipelineAsync(
        ConfiglueResourceContext context,
        CancellationToken cancellationToken = default
    )
    {
        var bucketName = ResolveBucket(context);
        var key = ResolveKey(context);
        try
        {
            var result = await S3ObjectClientExtensions
                .GetObjectStreamAsync(GetClient(context), bucketName, key, cancellationToken)
                .ConfigureAwait(false);
            return PipelineResourceReader.FromStream(result.Content, result.ETag, owner: result);
        }
        catch (AmazonS3Exception exception) when (IsMissingObject(exception))
        {
            return PipelineResourceReadResult.NotFound();
        }
    }

    /// <inheritdoc />
    public async ValueTask<ResourceReadResult> ReadAsync(
        CancellationToken cancellationToken = default
    ) => await ReadAsync(ConfiglueResourceContext.Default, cancellationToken).ConfigureAwait(false);

    /// <inheritdoc />
    public async ValueTask<ResourceReadResult> ReadAsync(
        ConfiglueResourceContext context,
        CancellationToken cancellationToken = default
    )
    {
        var bucketName = ResolveBucket(context);
        var key = ResolveKey(context);
        try
        {
            var result = await GetClient(context)
                .GetObjectAsync(bucketName, key, cancellationToken)
                .ConfigureAwait(false);
            return ResourceReadResult.Success(result.Content, result.ETag);
        }
        catch (AmazonS3Exception exception) when (IsMissingObject(exception))
        {
            return ResourceReadResult.NotFound();
        }
    }

    /// <inheritdoc />
    public async ValueTask<StateWriteResult> WriteAsync(
        ResourceWriteRequest request,
        CancellationToken cancellationToken = default
    ) =>
        await WriteAsync(ConfiglueResourceContext.Default, request, cancellationToken)
            .ConfigureAwait(false);

    /// <inheritdoc />
    public async ValueTask<StateWriteResult> WriteAsync(
        ConfiglueResourceContext context,
        ResourceWriteRequest request,
        CancellationToken cancellationToken = default
    )
    {
        var bucketName = ResolveBucket(context);
        var key = ResolveKey(context);
        var checkRevision = !request.Condition.IsNone;
        try
        {
            var result = await GetClient(context)
                .PutObjectAsync(
                    bucketName,
                    key,
                    request.Content,
                    checkRevision ? request.Condition.Revision : null,
                    checkRevision && request.Condition.Revision is null,
                    cancellationToken
                )
                .ConfigureAwait(false);
            return new StateWriteResult(result.ETag);
        }
        catch (AmazonS3Exception exception) when (checkRevision && IsRevisionConflict(exception))
        {
            throw new StateConflictException(
                $"The S3 object '{bucketName}/{key}' changed after it was read."
            );
        }
    }

    private IS3ObjectClient GetClient(ConfiglueResourceContext context) =>
        _clientSelector?.Invoke(context) ?? _client;

    private string ResolveBucket(ConfiglueResourceContext context)
    {
        var bucketName = _options.BucketNameSelector?.Invoke(context) ?? BucketName;
        ArgumentException.ThrowIfNullOrWhiteSpace(bucketName);
        return bucketName;
    }

    private string ResolveKey(ConfiglueResourceContext context)
    {
        var key = _options.KeySelector?.Invoke(context) ?? Key;
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        return key;
    }

    private static bool IsMissingObject(AmazonS3Exception exception) =>
        exception.ErrorCode is "NoSuchKey" or "NotFound"
        || (
            exception.StatusCode == HttpStatusCode.NotFound
            && string.IsNullOrEmpty(exception.ErrorCode)
        );

    private static bool IsRevisionConflict(AmazonS3Exception exception) =>
        exception.ErrorCode is "PreconditionFailed" or "ConditionalRequestConflict"
        || exception.StatusCode is HttpStatusCode.PreconditionFailed or HttpStatusCode.Conflict;

    private static ResourceId CreateResourceId(string bucketName, string key, string? route = null)
    {
        var identity = Encoding.UTF8.GetBytes(
            route is null ? bucketName + "\n" + key : bucketName + "\n" + key + "\n" + route
        );
        return new ResourceId(
            $"s3:{Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(identity)).ToLowerInvariant()}"
        );
    }

    private sealed class S3ObjectClient : IS3ObjectClient, IS3ObjectStreamClient
    {
        private readonly IAmazonS3 _client;

        public S3ObjectClient(IAmazonS3 client)
        {
            ArgumentNullException.ThrowIfNull(client);
            _client = client;
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

        public async Task<S3ObjectReadResult> GetObjectAsync(
            string bucketName,
            string key,
            CancellationToken cancellationToken
        )
        {
            using var response = await _client
                .GetObjectAsync(bucketName, key, cancellationToken)
                .ConfigureAwait(false);
            var length = response.ContentLength;
            if (length is > 0 and <= int.MaxValue)
            {
                var buffer = new byte[(int)length];
                await ReadExactlyAsync(response.ResponseStream, buffer, cancellationToken)
                    .ConfigureAwait(false);
                return new S3ObjectReadResult(buffer, response.ETag);
            }

            using var content = new MemoryStream();
            await response
                .ResponseStream.CopyToAsync(content, 81920, cancellationToken)
                .ConfigureAwait(false);
            return new S3ObjectReadResult(content.ToArray(), response.ETag);
        }

        public async Task<S3ObjectStreamResult> GetObjectStreamAsync(
            string bucketName,
            string key,
            CancellationToken cancellationToken
        )
        {
            var response = await _client
                .GetObjectAsync(bucketName, key, cancellationToken)
                .ConfigureAwait(false);
            return new S3ObjectStreamResult(response.ResponseStream, response.ETag, response);
        }

        public async Task<S3ObjectWriteResult> PutObjectAsync(
            string bucketName,
            string key,
            ReadOnlyMemory<byte> content,
            string? expectedETag,
            bool requireMissing,
            CancellationToken cancellationToken
        )
        {
            using var input = CreateReadOnlyStream(content);
            var request = new PutObjectRequest
            {
                BucketName = bucketName,
                Key = key,
                InputStream = input,
                IfMatch = expectedETag,
                IfNoneMatch = requireMissing ? "*" : null,
            };
            var response = await _client
                .PutObjectAsync(request, cancellationToken)
                .ConfigureAwait(false);
            return new S3ObjectWriteResult(response.ETag);
        }

        private static async Task ReadExactlyAsync(
            Stream stream,
            byte[] buffer,
            CancellationToken cancellationToken
        )
        {
            var offset = 0;
            while (offset < buffer.Length)
            {
                var read = await stream
                    .ReadAsync(buffer, offset, buffer.Length - offset, cancellationToken)
                    .ConfigureAwait(false);
                if (read == 0)
                {
                    throw new EndOfStreamException();
                }

                offset += read;
            }
        }
    }
}

internal interface IS3ObjectClient
{
    Task<S3ObjectReadResult> GetObjectAsync(
        string bucketName,
        string key,
        CancellationToken cancellationToken
    );

    Task<S3ObjectWriteResult> PutObjectAsync(
        string bucketName,
        string key,
        ReadOnlyMemory<byte> content,
        string? expectedETag,
        bool requireMissing,
        CancellationToken cancellationToken
    );
}

internal interface IS3ObjectStreamClient
{
    Task<S3ObjectStreamResult> GetObjectStreamAsync(
        string bucketName,
        string key,
        CancellationToken cancellationToken
    );
}

internal static class S3ObjectClientExtensions
{
    public static async Task<S3ObjectStreamResult> GetObjectStreamAsync(
        IS3ObjectClient client,
        string bucketName,
        string key,
        CancellationToken cancellationToken
    )
    {
        if (client is IS3ObjectStreamClient streamClient)
        {
            return await streamClient
                .GetObjectStreamAsync(bucketName, key, cancellationToken)
                .ConfigureAwait(false);
        }

        var result = await client
            .GetObjectAsync(bucketName, key, cancellationToken)
            .ConfigureAwait(false);
        var content =
            MemoryMarshal.TryGetArray(result.Content, out var segment) && segment.Array is not null
                ? new MemoryStream(segment.Array, segment.Offset, segment.Count, writable: false)
                : new MemoryStream(result.Content.ToArray(), writable: false);
        return new S3ObjectStreamResult(content, result.ETag);
    }
}

internal sealed class S3ObjectStreamResult : IDisposable
{
    private readonly IDisposable? _owner;

    public S3ObjectStreamResult(Stream content, string? eTag, IDisposable? owner = null)
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

internal sealed record S3ObjectReadResult
{
    public S3ObjectReadResult(ReadOnlyMemory<byte> content, string? eTag)
    {
        Content = content;
        ETag = eTag;
    }

    public ReadOnlyMemory<byte> Content { get; init; }

    public string? ETag { get; init; }
}

internal sealed record S3ObjectWriteResult
{
    public S3ObjectWriteResult(string? eTag)
    {
        ETag = eTag;
    }

    public string? ETag { get; init; }
}
