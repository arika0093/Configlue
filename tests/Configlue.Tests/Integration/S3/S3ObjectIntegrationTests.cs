using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Configlue.Resource.S3;

namespace Configlue.Tests;

/// <summary>
/// Exercises the production S3 path through the real AWS SDK adapter (request construction,
/// ETag revisions, conditional writes, and error translation) against an S3-compatible service.
/// </summary>
/// <remarks>
/// These tests validate S3-compatible semantics only. Behaviors that differ on real AWS S3
/// (for example IAM policy evaluation or AWS-specific error codes) are out of scope.
/// </remarks>
[S3Integration]
public sealed class S3ObjectIntegrationTests
{
    [Test]
    public async Task PutGetMissingAndConditionalWritesThroughRealSdk()
    {
        var bucket = NewBucketName();
        using var client = CreateClient();
        await CreateBucketAsync(client, bucket);
        try
        {
            var resource = new S3ObjectResource(client, bucket, "config.json");
            var context = ConfiglueResourceContext.Default;

            (await resource.ReadAsync(context)).Status.ShouldBe(StateReadStatus.NotFound);

            var created = await resource.WriteAsync(
                context,
                new ResourceWriteRequest(new byte[] { 1, 2, 3 }, RevisionCondition.MustNotExist)
            );
            created.Revision.ShouldNotBeNullOrEmpty();

            var read = await resource.ReadAsync(context);
            read.Status.ShouldBe(StateReadStatus.Success);
            read.Content.ToArray().ShouldBe(new byte[] { 1, 2, 3 });
            read.Revision.ShouldBe(created.Revision);

            await Should.ThrowAsync<StateConflictException>(async () =>
                await resource.WriteAsync(
                    context,
                    new ResourceWriteRequest(new byte[] { 4 }, RevisionCondition.MustNotExist)
                )
            );

            var updated = await resource.WriteAsync(
                context,
                new ResourceWriteRequest(
                    new byte[] { 5 },
                    RevisionCondition.Match(created.Revision!)
                )
            );
            updated.Revision.ShouldNotBeNullOrEmpty();
            updated.Revision.ShouldNotBe(created.Revision);

            await Should.ThrowAsync<StateConflictException>(async () =>
                await resource.WriteAsync(
                    context,
                    new ResourceWriteRequest(
                        new byte[] { 6 },
                        RevisionCondition.Match(created.Revision!)
                    )
                )
            );

            var unconditional = await resource.WriteAsync(
                context,
                new ResourceWriteRequest(new byte[] { 7 })
            );
            unconditional.Revision.ShouldNotBeNullOrEmpty();
            (await resource.ReadAsync(context)).Content.ToArray().ShouldBe(new byte[] { 7 });
        }
        finally
        {
            await CleanupAsync(client, bucket);
        }
    }

    [Test]
    public async Task SubjectAwareKeySelectionIsolatesObjects()
    {
        var bucket = NewBucketName();
        using var client = CreateClient();
        await CreateBucketAsync(client, bucket);
        try
        {
            var resource = new S3ObjectResource(
                client,
                bucket,
                "unused",
                new S3ObjectResourceOptions
                {
                    KeySelector = context => $"subjects/{context.Key.Value}/config.json",
                }
            );
            var first = CreateContext("tenant-a");
            var second = CreateContext("tenant-b");

            await resource.WriteAsync(
                first,
                new ResourceWriteRequest(new byte[] { 1 }, RevisionCondition.MustNotExist)
            );
            await resource.WriteAsync(
                second,
                new ResourceWriteRequest(new byte[] { 2 }, RevisionCondition.MustNotExist)
            );

            (await resource.ReadAsync(first)).Content.ToArray().ShouldBe(new byte[] { 1 });
            (await resource.ReadAsync(second)).Content.ToArray().ShouldBe(new byte[] { 2 });
            resource.GetResourceId(first).ShouldNotBe(resource.GetResourceId(second));

            var list = await client.ListObjectsV2Async(
                new ListObjectsV2Request { BucketName = bucket }
            );
            list.S3Objects.Count.ShouldBe(2);
        }
        finally
        {
            await CleanupAsync(client, bucket);
        }
    }

    [Test]
    public async Task CancellationSurfacesToTheCaller()
    {
        var bucket = NewBucketName();
        using var client = CreateClient();
        await CreateBucketAsync(client, bucket);
        try
        {
            var resource = new S3ObjectResource(client, bucket, "config.json");
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();

            await Should.ThrowAsync<OperationCanceledException>(async () =>
                await resource.ReadAsync(ConfiglueResourceContext.Default, cancellation.Token)
            );
        }
        finally
        {
            await CleanupAsync(client, bucket);
        }
    }

    private static string NewBucketName() =>
        ("configlue-itest-" + Guid.NewGuid().ToString("N")[..20]).ToLowerInvariant();

    private static AmazonS3Client CreateClient()
    {
        var serviceUrl = IntegrationEnvironment.S3ServiceUrl;
        var config = new AmazonS3Config
        {
            ServiceURL = serviceUrl,
            ForcePathStyle = true,
            UseHttp = serviceUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase),
        };
        return new AmazonS3Client(
            new BasicAWSCredentials(
                IntegrationEnvironment.S3AccessKey,
                IntegrationEnvironment.S3SecretKey
            ),
            config
        );
    }

    private static async Task CreateBucketAsync(AmazonS3Client client, string bucket)
    {
        await client.PutBucketAsync(new PutBucketRequest { BucketName = bucket });
    }

    private static async Task CleanupAsync(AmazonS3Client client, string bucket)
    {
        try
        {
            var list = await client.ListObjectsV2Async(
                new ListObjectsV2Request { BucketName = bucket }
            );
            foreach (var entry in list.S3Objects ?? [])
            {
                await client.DeleteObjectAsync(bucket, entry.Key);
            }

            await client.DeleteBucketAsync(bucket);
        }
        catch (AmazonS3Exception)
        {
            // Best-effort cleanup of the throwaway bucket.
        }
    }

    private static ConfiglueResourceContext CreateContext(string subject)
    {
        var key = SubjectKey.From(subject);
        return new ConfiglueResourceContext(new IntegrationSubject(key), key, RouteKey.Default);
    }

    private sealed record IntegrationSubject(SubjectKey Key) : IConfiglueSubject;
}
