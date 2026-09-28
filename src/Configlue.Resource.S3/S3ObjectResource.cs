using System.Net;
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
public sealed class S3ObjectResource : IResourceReader, IResourceWriter, IResourceIdentity
{
    private readonly IS3ObjectClient _client;

    /// <summary>Creates a resource for one object in an S3 bucket.</summary>
    public S3ObjectResource(
        IAmazonS3 client,
        string bucketName,
        string key,
        S3ObjectResourceOptions? options = null
    )
        : this(new S3ObjectClient(client), bucketName, key, options) { }

    internal S3ObjectResource(
        IS3ObjectClient client,
        string bucketName,
        string key,
        S3ObjectResourceOptions? options = null
    )
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentException.ThrowIfNullOrWhiteSpace(bucketName);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        _client = client;
        BucketName = bucketName;
        Key = key;
        ResourceId = options?.ResourceId ?? CreateResourceId(bucketName, key);
    }

    /// <summary>The S3 bucket name.</summary>
    public string BucketName { get; }

    /// <summary>The object key.</summary>
    public string Key { get; }

    /// <inheritdoc />
    public ResourceId ResourceId { get; }

    /// <inheritdoc />
    public async ValueTask<ResourceReadResult> ReadAsync(
        CancellationToken cancellationToken = default
    )
    {
        try
        {
            var result = await _client
                .GetObjectAsync(BucketName, Key, cancellationToken)
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
    )
    {
        var checkRevision = request.CheckRevision || request.ExpectedRevision is not null;
        try
        {
            var result = await _client
                .PutObjectAsync(
                    BucketName,
                    Key,
                    request.Content,
                    checkRevision ? request.ExpectedRevision : null,
                    checkRevision && request.ExpectedRevision is null,
                    cancellationToken
                )
                .ConfigureAwait(false);
            return new StateWriteResult(result.ETag);
        }
        catch (AmazonS3Exception exception) when (checkRevision && IsRevisionConflict(exception))
        {
            throw new StateConflictException(
                $"The S3 object '{BucketName}/{Key}' changed after it was read."
            );
        }
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

    private static ResourceId CreateResourceId(string bucketName, string key)
    {
        var identity = Encoding.UTF8.GetBytes(bucketName + "\n" + key);
        return new ResourceId(
            $"s3:{Convert.ToHexString(SHA256.HashData(identity)).ToLowerInvariant()}"
        );
    }

    private sealed class S3ObjectClient : IS3ObjectClient
    {
        private readonly IAmazonS3 _client;

        public S3ObjectClient(IAmazonS3 client)
        {
            ArgumentNullException.ThrowIfNull(client);
            _client = client;
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
            using var content = new MemoryStream();
            await response
                .ResponseStream.CopyToAsync(content, cancellationToken)
                .ConfigureAwait(false);
            return new S3ObjectReadResult(content.ToArray(), response.ETag);
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
            using var input = new MemoryStream(content.ToArray(), writable: false);
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
