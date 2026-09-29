using System.Buffers;
using System.Net;
using Amazon.S3;
using Configlue.Resource.S3;

namespace Configlue.Tests;

public sealed class S3ObjectResourceTests
{
    [Test]
    public async Task ReadPipelineAsync_ReturnsContentAndETag()
    {
        var client = new FakeS3ObjectClient
        {
            ReadResult = new S3ObjectReadResult(new byte[] { 1, 2, 3 }, "\"revision-1\""),
        };
        var resource = new S3ObjectResource(client, "bucket", "settings.json");

        await using var result = await resource.ReadPipelineAsync();
        var content = await result.ReadAllAsync();
        var bytes = new byte[3];
        content.CopyTo(bytes);
        result.Content!.AdvanceTo(content.End);

        resource.IsPipelineReadPreferred.ShouldBeTrue();
        result.Status.ShouldBe(StateReadStatus.Success);
        bytes.ShouldBe(new byte[] { 1, 2, 3 });
        result.Revision.ShouldBe("\"revision-1\"");
    }

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
    public async Task SubjectAwareAddressSelectsObjectAndKeepsItsEtagCondition()
    {
        var client = new FakeS3ObjectClient
        {
            ReadResult = new S3ObjectReadResult(new byte[] { 1, 2 }, "\"revision-1\""),
            WriteResult = new S3ObjectWriteResult("\"revision-2\""),
        };
        var resource = new S3ObjectResource(
            client,
            "settings-default",
            "settings.json",
            new S3ObjectResourceOptions
            {
                BucketNameSelector = context => $"settings-{context.Route.Value}",
                KeySelector = context => $"{context.Key.Value}/settings.json",
            }
        );
        var subject = new ResourceSubject("tenant-a");
        var context = new ConfiglueResourceContext(
            subject,
            subject.Key,
            RouteKey.From("region-jp")
        );

        var read = await resource.ReadAsync(context);
        var write = await resource.WriteAsync(
            context,
            new ResourceWriteRequest(
                new byte[] { 3 },
                Condition: RevisionCondition.FromRevision(read.Revision)
            )
        );

        read.Content.ToArray().ShouldBe([1, 2]);
        client.LastBucketName.ShouldBe("settings-region-jp");
        client.LastKey.ShouldBe($"{subject.Key.Value}/settings.json");
        client.LastExpectedETag.ShouldBe("\"revision-1\"");
        write.Revision.ShouldBe("\"revision-2\"");
        resource.GetResourceId(context).ShouldNotBe(resource.ResourceId);
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
                Condition: RevisionCondition.FromRevision("\"revision-1\"")
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
                    Condition: RevisionCondition.FromRevision("\"stale\"")
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
            new ResourceWriteRequest(new byte[] { 1 }, Condition: RevisionCondition.MustNotExist)
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
        public string? LastBucketName { get; private set; }
        public string? LastKey { get; private set; }

        public Task<S3ObjectReadResult> GetObjectAsync(
            string bucketName,
            string key,
            CancellationToken cancellationToken
        ) =>
            CaptureAddress(bucketName, key) && ReadException is null
                ? Task.FromResult(ReadResult)
                : Task.FromException<S3ObjectReadResult>(ReadException!);

        public Task<S3ObjectWriteResult> PutObjectAsync(
            string bucketName,
            string key,
            ReadOnlyMemory<byte> content,
            string? expectedETag,
            bool requireMissing,
            CancellationToken cancellationToken
        )
        {
            LastBucketName = bucketName;
            LastKey = key;
            LastContent = content;
            LastExpectedETag = expectedETag;
            LastRequireMissing = requireMissing;
            return WriteException is null
                ? Task.FromResult(WriteResult)
                : Task.FromException<S3ObjectWriteResult>(WriteException);
        }

        private bool CaptureAddress(string bucketName, string key)
        {
            LastBucketName = bucketName;
            LastKey = key;
            return true;
        }
    }

    private sealed record ResourceSubject(string Name) : IConfiglueSubject
    {
        public SubjectKey Key => SubjectKey.From(Name);
    }
}
