using System.Net;
using Amazon.S3;
using Configlue.Resource.S3;

namespace Configlue.Tests;

public sealed class S3ObjectResourceTests
{
    [Test]
    public async Task ReadAsync_ReturnsContentAndETag()
    {
        var client = new FakeS3ObjectClient
        {
            ReadResult = new S3ObjectReadResult(new byte[] { 1, 2, 3 }, "\"revision-1\""),
        };
        var resource = new S3ObjectResource(client, "bucket", "settings.json");

        var result = await resource.ReadAsync();

        result.Status.ShouldBe(StateReadStatus.Success);
        result.Content.ToArray().ShouldBe(new byte[] { 1, 2, 3 });
        result.Revision.ShouldBe("\"revision-1\"");
    }

    [Test]
    public async Task ReadAsync_MapsMissingObjectToNotFound()
    {
        var client = new FakeS3ObjectClient { ReadException = S3Exception("NoSuchKey", 404) };
        var resource = new S3ObjectResource(client, "bucket", "missing.json");

        var result = await resource.ReadAsync();

        result.Status.ShouldBe(StateReadStatus.NotFound);
    }

    [Test]
    public async Task ReadAsync_DoesNotHideMissingBucket()
    {
        var client = new FakeS3ObjectClient { ReadException = S3Exception("NoSuchBucket", 404) };
        var resource = new S3ObjectResource(client, "missing", "settings.json");

        await Should.ThrowAsync<AmazonS3Exception>(async () => await resource.ReadAsync());
    }

    [Test]
    public async Task WriteAsync_UsesExpectedETagAndMapsPreconditionFailure()
    {
        var client = new FakeS3ObjectClient
        {
            WriteResult = new S3ObjectWriteResult("\"revision-2\""),
        };
        var resource = new S3ObjectResource(client, "bucket", "settings.json");

        var result = await resource.WriteAsync(
            new ResourceWriteRequest(
                new byte[] { 4, 5 },
                ExpectedRevision: "\"revision-1\"",
                CheckRevision: true
            )
        );

        client.LastExpectedETag.ShouldBe("\"revision-1\"");
        client.LastRequireMissing.ShouldBeFalse();
        client.LastContent.ToArray().ShouldBe(new byte[] { 4, 5 });
        result.Revision.ShouldBe("\"revision-2\"");

        client.WriteException = S3Exception("PreconditionFailed", 412);
        await Should.ThrowAsync<StateConflictException>(async () =>
            await resource.WriteAsync(
                new ResourceWriteRequest(
                    new byte[] { 6 },
                    ExpectedRevision: "\"stale\"",
                    CheckRevision: true
                )
            )
        );
    }

    [Test]
    public async Task WriteAsync_RequiresMissingObjectWhenRevisionCheckHasNoRevision()
    {
        var client = new FakeS3ObjectClient();
        var resource = new S3ObjectResource(client, "bucket", "settings.json");

        await resource.WriteAsync(
            new ResourceWriteRequest(new byte[] { 1 }, ExpectedRevision: null, CheckRevision: true)
        );

        client.LastExpectedETag.ShouldBeNull();
        client.LastRequireMissing.ShouldBeTrue();
    }

    [Test]
    public async Task WriteAsync_IsUnconditionalUnlessRevisionCheckIsRequested()
    {
        var client = new FakeS3ObjectClient();
        var resource = new S3ObjectResource(client, "bucket", "settings.json");

        await resource.WriteAsync(new ResourceWriteRequest(new byte[] { 9 }));

        client.LastExpectedETag.ShouldBeNull();
        client.LastRequireMissing.ShouldBeFalse();
    }

    [Test]
    public void ResourceId_IsStableForBucketAndKeyAndCanBeOverridden()
    {
        var first = new S3ObjectResource(new FakeS3ObjectClient(), "bucket", "settings.json");
        var same = new S3ObjectResource(new FakeS3ObjectClient(), "bucket", "settings.json");
        var different = new S3ObjectResource(new FakeS3ObjectClient(), "bucket", "other.json");
        var overridden = new ResourceId("deployment:settings");
        var withOverride = new S3ObjectResource(
            new FakeS3ObjectClient(),
            "bucket",
            "settings.json",
            new S3ObjectResourceOptions { ResourceId = overridden }
        );

        same.ResourceId.ShouldBe(first.ResourceId);
        different.ResourceId.ShouldNotBe(first.ResourceId);
        withOverride.ResourceId.ShouldBe(overridden);
    }

    private static AmazonS3Exception S3Exception(string errorCode, int statusCode) =>
        new(errorCode) { ErrorCode = errorCode, StatusCode = (HttpStatusCode)statusCode };

    private sealed class FakeS3ObjectClient : IS3ObjectClient
    {
        public S3ObjectReadResult ReadResult { get; init; } = new(default, null);
        public S3ObjectWriteResult WriteResult { get; init; } = new(null);
        public AmazonS3Exception? ReadException { get; init; }
        public AmazonS3Exception? WriteException { get; set; }
        public string? LastExpectedETag { get; private set; }
        public bool LastRequireMissing { get; private set; }
        public ReadOnlyMemory<byte> LastContent { get; private set; }

        public Task<S3ObjectReadResult> GetObjectAsync(
            string bucketName,
            string key,
            CancellationToken cancellationToken
        ) =>
            ReadException is null
                ? Task.FromResult(ReadResult)
                : Task.FromException<S3ObjectReadResult>(ReadException);

        public Task<S3ObjectWriteResult> PutObjectAsync(
            string bucketName,
            string key,
            ReadOnlyMemory<byte> content,
            string? expectedETag,
            bool requireMissing,
            CancellationToken cancellationToken
        )
        {
            LastContent = content;
            LastExpectedETag = expectedETag;
            LastRequireMissing = requireMissing;
            return WriteException is null
                ? Task.FromResult(WriteResult)
                : Task.FromException<S3ObjectWriteResult>(WriteException);
        }
    }
}
