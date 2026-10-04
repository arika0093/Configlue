using System.Buffers;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Configlue.Provider.Json;
using Configlue.Resource.Gcs;
using Google.Cloud.Storage.V1;

namespace Configlue.Tests;

public sealed class GcsObjectResourceTests
{
    [Test]
    public async Task ReadAsync_ReturnsContentGenerationAndMetadata()
    {
        var client = new FakeGcsObjectClient();
        var seeded = client.Seed(
            "bucket",
            "settings.json",
            Encoding.UTF8.GetBytes("""{"RetryCount":3}"""),
            contentType: "application/json"
        );
        using var resource = CreateResource(client);

        var result = await resource.ReadAsync();

        result.Status.ShouldBe(StateReadStatus.Success);
        Encoding.UTF8.GetString(result.Content.Span).ShouldBe("""{"RetryCount":3}""");
        result.Revision.ShouldBe(seeded.Revision);
        seeded.Metadata.Generation.ShouldNotBeNull();
        seeded.Metadata.Metageneration.ShouldBe(1);
        seeded.Metadata.ETag.ShouldBe($"etag-{seeded.Metadata.Generation}");
        seeded.Metadata.Updated.ShouldNotBeNull();
        seeded.Metadata.ContentType.ShouldBe("application/json");
        seeded.Metadata.Size.ShouldBe(result.Content.Length);
    }

    [Test]
    public async Task ReadAsync_PassesThroughBuffersWithoutCopying()
    {
        var client = new FakeGcsObjectClient();
        var payload = new byte[] { 1, 2, 3, 4 };
        client.Seed("bucket", "settings.json", payload);
        using var resource = CreateResource(client);

        var result = await resource.ReadAsync();

        result.Status.ShouldBe(StateReadStatus.Success);
        MemoryMarshal.TryGetArray(result.Content, out var segment).ShouldBeTrue();
        ReferenceEquals(segment.Array, payload).ShouldBeTrue();
        client.BufferedReadCallCount.ShouldBe(1);
        client.MetadataCallCount.ShouldBe(0);
    }

    [Test]
    public async Task ReadAsync_MapsMissingObjectToNotFound()
    {
        using var resource = CreateResource(new FakeGcsObjectClient());

        var result = await resource.ReadAsync();

        result.Status.ShouldBe(StateReadStatus.NotFound);
    }

    [Test]
    public async Task ReadPipelineAsync_StreamsContentWithRevision()
    {
        var client = new FakeGcsObjectClient();
        var seeded = client.Seed("bucket", "settings.json", new byte[] { 1, 2, 3, 4 });
        using var resource = CreateResource(client);

        resource.IsPipelineReadPreferred.ShouldBeTrue();
        await using var result = await resource.ReadPipelineAsync();
        var content = await result.ReadAllAsync();
        var bytes = new byte[4];
        content.CopyTo(bytes);
        result.Content!.AdvanceTo(content.End);

        result.Status.ShouldBe(StateReadStatus.Success);
        bytes.ShouldBe(new byte[] { 1, 2, 3, 4 });
        result.Revision.ShouldBe(seeded.Revision);
    }

    [Test]
    public async Task ReadPipelineAsync_MapsMissingObjectToNotFound()
    {
        using var resource = CreateResource(new FakeGcsObjectClient());

        await using var result = await resource.ReadPipelineAsync();

        result.Status.ShouldBe(StateReadStatus.NotFound);
    }

    [Test]
    public async Task SubjectAwareAddressing_SelectsBucketAndObjectAndKeepsGenerationCondition()
    {
        var client = new FakeGcsObjectClient();
        var subject = new ResourceSubject("tenant-a");
        var context = new ConfiglueResourceContext(
            subject,
            ResourceKey.From(subject.Key),
            RouteKey.From("region-jp")
        );
        client.Seed(
            "settings-region-jp",
            $"{subject.Key.Value}/settings.json",
            new byte[] { 1, 2 }
        );
        using var resource = new GcsObjectResource(
            client,
            "settings-default",
            "settings.json",
            new GcsObjectResourceOptions
            {
                BucketNameSelector = context => $"settings-{context.Route.Value}",
                ObjectNameSelector = context => $"{context.ResourceKey.Value}/settings.json",
                PollInterval = TimeSpan.FromMilliseconds(10),
            }
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
        client.LastObjectName.ShouldBe($"{subject.Key.Value}/settings.json");
        client.LastExpectedGeneration.ShouldBe(
            long.Parse(read.Revision!, CultureInfo.InvariantCulture)
        );
        write.Revision.ShouldNotBe(read.Revision);
        using var defaultResource = new GcsObjectResource(
            new FakeGcsObjectClient(),
            "settings-default",
            "settings.json"
        );
        resource
            .GetResourceId(context)
            .ShouldNotBe(defaultResource.GetResourceId(ConfiglueResourceContext.Default));
    }

    [Test]
    public async Task WriteAsync_UsesExpectedGenerationAndMapsStaleToConflict()
    {
        var client = new FakeGcsObjectClient();
        var seeded = client.Seed("bucket", "settings.json", new byte[] { 1 });
        using var resource = CreateResource(client);

        var result = await resource.WriteAsync(
            new ResourceWriteRequest(
                new byte[] { 4, 5 },
                Condition: RevisionCondition.FromRevision(seeded.Revision)
            )
        );

        client.LastExpectedGeneration.ShouldBe(
            long.Parse(seeded.Revision!, CultureInfo.InvariantCulture)
        );
        client.LastRequireMissing.ShouldBeFalse();
        client.LastContent.ToArray().ShouldBe(new byte[] { 4, 5 });
        result.Revision.ShouldNotBeNullOrEmpty();
        result.Revision.ShouldNotBe(seeded.Revision);

        await Should.ThrowAsync<StateConflictException>(async () =>
            await resource.WriteAsync(
                new ResourceWriteRequest(
                    new byte[] { 6 },
                    Condition: RevisionCondition.FromRevision(seeded.Revision)
                )
            )
        );
    }

    [Test]
    public async Task WriteAsync_RejectsUnparseableRevisionAsConflict()
    {
        var client = new FakeGcsObjectClient();
        client.Seed("bucket", "settings.json", new byte[] { 1 });
        using var resource = CreateResource(client);

        await Should.ThrowAsync<StateConflictException>(async () =>
            await resource.WriteAsync(
                new ResourceWriteRequest(
                    new byte[] { 2 },
                    Condition: RevisionCondition.Match("\"opaque-etag\"")
                )
            )
        );

        client.BufferedReadCallCount.ShouldBe(0);
    }

    [Test]
    public async Task WriteAsync_CreateOnlySemantics()
    {
        var client = new FakeGcsObjectClient();
        using var resource = CreateResource(client);

        var created = await resource.WriteAsync(
            new ResourceWriteRequest(new byte[] { 1 }, Condition: RevisionCondition.MustNotExist)
        );

        client.LastExpectedGeneration.ShouldBeNull();
        client.LastRequireMissing.ShouldBeTrue();
        created.Revision.ShouldNotBeNullOrEmpty();

        await Should.ThrowAsync<StateConflictException>(async () =>
            await resource.WriteAsync(
                new ResourceWriteRequest(
                    new byte[] { 2 },
                    Condition: RevisionCondition.MustNotExist
                )
            )
        );
    }

    [Test]
    public async Task WriteAsync_IsUnconditionalUnlessRevisionCheckIsRequested()
    {
        var client = new FakeGcsObjectClient();
        client.Seed("bucket", "settings.json", new byte[] { 1 });
        using var resource = CreateResource(client);

        await resource.WriteAsync(new ResourceWriteRequest(new byte[] { 9 }));

        client.LastExpectedGeneration.ShouldBeNull();
        client.LastRequireMissing.ShouldBeFalse();
    }

    [Test]
    public async Task WriteAsync_PreservesContentTypeOnOverwriteByDefault()
    {
        var client = new FakeGcsObjectClient();
        client.Seed(
            "bucket",
            "settings.json",
            new byte[] { 1 },
            contentType: "application/json"
        );
        using var resource = CreateResource(client);

        await resource.WriteAsync(new ResourceWriteRequest(new byte[] { 2 }));

        client.LastContentType.ShouldBe("application/json");
    }

    [Test]
    public async Task WriteAsync_AppliesExplicitContentType()
    {
        var client = new FakeGcsObjectClient();
        client.Seed(
            "bucket",
            "settings.json",
            new byte[] { 1 },
            contentType: "application/json"
        );
        using var resource = CreateResource(
            client,
            options: new GcsObjectResourceOptions
            {
                ContentType = "application/octet-stream",
                PollInterval = TimeSpan.FromMilliseconds(10),
            }
        );

        await resource.WriteAsync(new ResourceWriteRequest(new byte[] { 2 }));

        client.LastContentType.ShouldBe("application/octet-stream");
        var reread = await resource.ReadAsync();
        reread.Status.ShouldBe(StateReadStatus.Success);
    }

    [Test]
    public async Task WriteAsync_RefreshesGenerationMetadataAndSize()
    {
        var client = new FakeGcsObjectClient();
        var seeded = client.Seed("bucket", "settings.json", new byte[] { 1, 2 });
        using var resource = CreateResource(client);

        var written = await resource.WriteAsync(new ResourceWriteRequest(new byte[] { 1, 2, 3 }));

        written.Revision.ShouldNotBe(seeded.Revision);
        var reread = await resource.ReadAsync();
        reread.Revision.ShouldBe(written.Revision);
        reread.Content.ToArray().ShouldBe(new byte[] { 1, 2, 3 });
        client.StoredMetadata("bucket", "settings.json")!.Size.ShouldBe(3);
        client
            .StoredMetadata("bucket", "settings.json")!
            .Metageneration.ShouldBe(seeded.Metadata.Metageneration + 1);
    }

    [Test]
    public void ResourceId_IsStableForBucketAndObjectAndCanBeOverridden()
    {
        using var first = CreateResource(new FakeGcsObjectClient());
        using var same = CreateResource(new FakeGcsObjectClient());
        using var different = new GcsObjectResource(
            new FakeGcsObjectClient(),
            "bucket",
            "other.json"
        );
        var overridden = new ResourceId("deployment:settings");
        using var withOverride = new GcsObjectResource(
            new FakeGcsObjectClient(),
            "bucket",
            "settings.json",
            new GcsObjectResourceOptions { FixedResourceId = overridden }
        );

        var context = ConfiglueResourceContext.Default;
        same.GetResourceId(context).ShouldBe(first.GetResourceId(context));
        different.GetResourceId(context).ShouldNotBe(first.GetResourceId(context));
        withOverride.GetResourceId(context).ShouldBe(overridden);
        first.GetResourceId(context).Value.ShouldStartWith("gcs:");
    }

    [Test]
    public void Constructor_RejectsInvalidIdentityAndPolling()
    {
        Should.Throw<ArgumentNullException>(() =>
            new GcsObjectResource((StorageClient)null!, "bucket", "settings.json")
        );
        Should.Throw<ArgumentNullException>(() =>
            new GcsObjectResource((IGcsObjectClient)null!, "bucket", "settings.json")
        );
        Should.Throw<ArgumentException>(() =>
            new GcsObjectResource(new StubStorageClient(), "", "settings.json")
        );
        Should.Throw<ArgumentException>(() =>
            new GcsObjectResource(new StubStorageClient(), "bucket", "")
        );
        Should.Throw<ArgumentOutOfRangeException>(() =>
            new GcsObjectResource(
                new FakeGcsObjectClient(),
                "bucket",
                "settings.json",
                new GcsObjectResourceOptions { PollInterval = TimeSpan.Zero }
            )
        );
        Should.Throw<ArgumentOutOfRangeException>(() =>
            new GcsObjectResource(
                new FakeGcsObjectClient(),
                "bucket",
                "settings.json",
                new GcsObjectResourceOptions
                {
                    PollInterval = TimeSpan.FromMilliseconds(-1),
                }
            )
        );
    }

    [Test]
    public async Task InjectedClientSelector_RoutesOperationsPerClient()
    {
        var primary = new FakeGcsObjectClient();
        primary.Seed("bucket", "settings.json", new byte[] { 1 });
        var alternate = new FakeGcsObjectClient();
        alternate.Seed("bucket", "settings.json", new byte[] { 2 });
        using var resource = new GcsObjectResource(
            primary,
            "bucket",
            "settings.json",
            new GcsObjectResourceOptions { PollInterval = TimeSpan.FromMilliseconds(10) },
            context => context.Route.Value == "alt" ? alternate : primary
        );
        var primaryContext = CreateRoutedContext("tenant-a", "primary");
        var alternateContext = CreateRoutedContext("tenant-a", "alt");

        (await resource.ReadAsync(primaryContext)).Content.ToArray().ShouldBe(new byte[] { 1 });
        (await resource.ReadAsync(alternateContext)).Content.ToArray().ShouldBe(new byte[] { 2 });
        resource
            .GetResourceId(primaryContext)
            .ShouldNotBe(resource.GetResourceId(alternateContext));
    }

    [Test]
    public void FixedClientIdentity_IgnoresRoutes()
    {
        using var resource = CreateResource(new FakeGcsObjectClient());
        var first = CreateRoutedContext("tenant-a", "primary");
        var second = CreateRoutedContext("tenant-a", "alt");

        resource.GetResourceId(first).ShouldBe(resource.GetResourceId(second));
    }

    [Test]
    public async Task Watcher_ReturnsImmediatelyWhenGenerationAlreadyChanged()
    {
        var client = new FakeGcsObjectClient();
        var seeded = client.Seed("bucket", "settings.json", new byte[] { 1 });
        using var resource = CreateResource(client);

        await resource.WaitForChangeAsync(
            ConfiglueResourceContext.Default,
            "stale-generation"
        );

        (await resource.ReadAsync()).Revision.ShouldBe(seeded.Revision);
    }

    [Test]
    public async Task Watcher_PollsMetadataUntilGenerationChangesWithoutBodyDownloads()
    {
        var client = new FakeGcsObjectClient();
        var seeded = client.Seed("bucket", "settings.json", new byte[] { 1 });
        using var resource = CreateResource(client);
        var context = ConfiglueResourceContext.Default;

        var wait = resource.WaitForChangeAsync(context, seeded.Revision).AsTask();
        await Task.Delay(60);
        wait.IsCompleted.ShouldBeFalse();
        client.DownloadCallCount.ShouldBe(0);

        client.FlipGeneration("bucket", "settings.json", new byte[] { 2 });
        await wait;

        client.DownloadCallCount.ShouldBe(0);
        (client.MetadataCallCount >= 2).ShouldBeTrue();
        (await resource.ReadAsync(context)).Content.ToArray().ShouldBe(new byte[] { 2 });
    }

    [Test]
    public async Task Watcher_ReturnsWhenObjectAppearsAndWhenItIsDeleted()
    {
        var client = new FakeGcsObjectClient();
        using var resource = CreateResource(client);
        var context = ConfiglueResourceContext.Default;

        var appear = resource.WaitForChangeAsync(context, null).AsTask();
        await Task.Delay(30);
        appear.IsCompleted.ShouldBeFalse();
        var seeded = client.Seed("bucket", "settings.json", new byte[] { 1 });
        await appear;

        var disappear = resource.WaitForChangeAsync(context, seeded.Revision).AsTask();
        await Task.Delay(30);
        disappear.IsCompleted.ShouldBeFalse();
        client.Delete("bucket", "settings.json");
        await disappear;

        (await resource.ReadAsync(context)).Status.ShouldBe(StateReadStatus.NotFound);
    }

    [Test]
    public async Task Watcher_RespectsCancellation()
    {
        var client = new FakeGcsObjectClient();
        var seeded = client.Seed("bucket", "settings.json", new byte[] { 1 });
        using var resource = CreateResource(client);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Should.ThrowAsync<OperationCanceledException>(async () =>
            await resource.WaitForChangeAsync(
                ConfiglueResourceContext.Default,
                seeded.Revision,
                cancellation.Token
            )
        );
    }

    [Test]
    public async Task Watcher_DisposeWakesWaitersAndRejectsNewWaits()
    {
        var client = new FakeGcsObjectClient();
        var seeded = client.Seed("bucket", "settings.json", new byte[] { 1 });
        var resource = CreateResource(client);

        var wait = resource.WaitForChangeAsync(
            ConfiglueResourceContext.Default,
            seeded.Revision
        ).AsTask();
        await Task.Delay(50);
        wait.IsCompleted.ShouldBeFalse();

        resource.Dispose();
        await wait;

        Should.Throw<ObjectDisposedException>(() =>
            resource
                .WaitForChangeAsync(ConfiglueResourceContext.Default, seeded.Revision)
                .AsTask()
        );
        resource.Dispose();
    }

    [Test]
    public async Task Cancellation_SurfacesFromReadsAndWrites()
    {
        var client = new FakeGcsObjectClient();
        client.Seed("bucket", "settings.json", new byte[] { 1 });
        using var resource = CreateResource(client);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Should.ThrowAsync<OperationCanceledException>(async () =>
            await resource.ReadAsync(ConfiglueResourceContext.Default, cancellation.Token)
        );
        await Should.ThrowAsync<OperationCanceledException>(async () =>
            await resource.ReadPipelineAsync(
                ConfiglueResourceContext.Default,
                cancellation.Token
            )
        );
        await Should.ThrowAsync<OperationCanceledException>(async () =>
            await resource.WriteAsync(
                ConfiglueResourceContext.Default,
                new ResourceWriteRequest(new byte[] { 2 }),
                cancellation.Token
            )
        );
    }

    [Test]
    public async Task TransformerAndCodec_ComposeOverPipelineAndBufferedReads()
    {
        var client = new FakeGcsObjectClient();
        using var resource = CreateResource(client);
        var codec = new JsonStateCodec<AppSettings.Fragment>();
        var transforming = new TransformingResource(resource, [new PrefixTransformer()]);
        var buffered = new SerializedStateReader<AppSettings.Fragment>(transforming, codec);
        var fragment = new AppSettings.Fragment
        {
            RetryCount = Optional<int>.Present(7),
        };

        await transforming.Writer!.WriteAsync(new ResourceWriteRequest(Serialize(fragment)));

        var stored = client.StoredContent("bucket", "settings.json");
        Encoding.UTF8.GetString(stored, 0, 4).ShouldBe("GCS:");
        (await buffered.ReadAsync()).Value!.RetryCount.ShouldBe(7);

        client.Seed("bucket", "plain.json", Serialize(fragment));
        using var plain = CreateResource(client, name: "plain.json");
        var streaming = new SerializedStateReader<AppSettings.Fragment>(
            plain,
            new JsonStateCodec<AppSettings.Fragment> { UseAsyncStreamDecoding = true }
        );
        var streamed = await streaming.ReadAsync();
        streamed.Status.ShouldBe(StateReadStatus.Success);
        streamed.Value!.RetryCount.ShouldBe(7);
        streamed.Revision.ShouldBe((await plain.ReadAsync()).Revision);
    }

    [Test]
    public async Task LargePayload_RoundTripsBufferedAndStreaming()
    {
        var client = new FakeGcsObjectClient();
        using var resource = CreateResource(client);
        var payload = CreateDeterministicPayload(2_000_000);
        var expectedHash = Hash(payload);

        await resource.WriteAsync(new ResourceWriteRequest(payload));

        var buffered = await resource.ReadAsync();
        buffered.Status.ShouldBe(StateReadStatus.Success);
        Hash(buffered.Content.ToArray()).ShouldBe(expectedHash);

        await using var streamed = await resource.ReadPipelineAsync();
        var content = await streamed.ReadAllAsync();
        Hash(content.ToArray()).ShouldBe(expectedHash);
        streamed.Content!.AdvanceTo(content.End);
    }

    [Test]
    public async Task AllocationPaths_HandleEmptyAndBoundaryPayloads()
    {
        var client = new FakeGcsObjectClient();
        using var resource = CreateResource(client);

        await resource.WriteAsync(new ResourceWriteRequest(ReadOnlyMemory<byte>.Empty));
        var empty = await resource.ReadAsync();
        empty.Status.ShouldBe(StateReadStatus.Success);
        empty.Content.Length.ShouldBe(0);
        empty.Revision.ShouldNotBeNullOrEmpty();

        foreach (var size in new[] { 1, 65_537, 100_000 })
        {
            var payload = CreateDeterministicPayload(size);
            await resource.WriteAsync(new ResourceWriteRequest(payload));
            var reread = await resource.ReadAsync();
            reread.Content.ToArray().ShouldBe(payload);
            await using var streamed = await resource.ReadPipelineAsync();
            var content = await streamed.ReadAllAsync();
            content.ToArray().ShouldBe(payload);
            streamed.Content!.AdvanceTo(content.End);
        }
    }

    [Test]
    public void SourceRegistration_RejectsConflictingClientConfiguration()
    {
        var sources = new ConfiglueSourceSetBuilder();
        using var client = new StubStorageClient();
        var options = new GcsObjectSourceOptions
        {
            BucketName = "bucket",
            ObjectName = "settings.json",
            Client = client,
            ClientFactory = _ => client,
            Codec = StateCodecBinding.Typed(new JsonStateCodec<AppSettings.Fragment>()),
        };

        Should.Throw<ArgumentException>(() => sources.FromGcsObject(options));
    }

    [Test]
    public void SourceRegistration_RejectsInvalidOptions()
    {
        var sources = new ConfiglueSourceSetBuilder();
        using var client = new StubStorageClient();

        Should.Throw<ArgumentNullException>(() => sources.FromGcsObject(null!));
        Should.Throw<ArgumentException>(() =>
            sources.FromGcsObject(
                new GcsObjectSourceOptions
                {
                    BucketName = "",
                    ObjectName = "settings.json",
                    Client = client,
                    Codec = StateCodecBinding.Typed(
                        new JsonStateCodec<AppSettings.Fragment>()
                    ),
                }
            )
        );
        Should.Throw<ArgumentException>(() =>
            sources.FromGcsObject(
                new GcsObjectSourceOptions
                {
                    BucketName = "bucket",
                    ObjectName = " ",
                    Client = client,
                    Codec = StateCodecBinding.Typed(
                        new JsonStateCodec<AppSettings.Fragment>()
                    ),
                }
            )
        );
        Should.Throw<ArgumentNullException>(() =>
            sources.FromGcsObject(
                new GcsObjectSourceOptions
                {
                    BucketName = "bucket",
                    ObjectName = "settings.json",
                    Client = client,
                    Codec = null!,
                }
            )
        );
    }

    [Test]
    public async Task SourceRegistration_ExposesBucketOriginWithoutCredentialMaterial()
    {
        using var client = new StubStorageClient();
        var factoryInvocations = 0;
        await using var context = ConfiglueApp.CreateContext(builder =>
        {
            builder.Add<AppSettings>(model =>
                model.Sources(sources =>
                {
                    sources.FromGcsObject(
                        new GcsObjectSourceOptions
                        {
                            BucketName = "settings",
                            ObjectName = "one.json",
                            Client = client,
                            Codec = StateCodecBinding.Typed(
                                new JsonStateCodec<AppSettings.Fragment>()
                            ),
                            Writable = false,
                        }
                    );
                    sources.FromGcsObject(
                        new GcsObjectSourceOptions
                        {
                            BucketName = "settings",
                            ObjectName = "two.json",
                            ClientFactory = _ =>
                            {
                                factoryInvocations++;
                                return client;
                            },
                            Codec = StateCodecBinding.Typed(
                                new JsonStateCodec<AppSettings.Fragment>()
                            ),
                            Writable = false,
                            Priority = 100,
                        }
                    );
                })
            );
        });

        var diagnostics = context.GetRuntimeState<AppSettings>().GetDiagnostics().Sources;
        diagnostics.Count.ShouldBe(2);
        var origins = diagnostics.Select(static source => source.PhysicalOrigin).ToList();
        string.Join(";", origins.Select(static origin => origin ?? "<null>"))
            .ShouldBe("gcs:settings;gcs:settings");
        diagnostics.Select(static source => source.CanWatch).ShouldBe([true, true]);

        // Source creation is lazy: the factory runs when the runtime first materializes
        // its source. The stub client throws on use, which proves the factory ran.
        Exception? observed = null;
        try
        {
            await context.GetRuntimeState<AppSettings>().GetValueAsync();
        }
        catch (Exception exception)
        {
            observed = exception;
        }

        (observed?.GetType().FullName ?? "<no exception>").ShouldBe(
            typeof(NotImplementedException).FullName
        );
        factoryInvocations.ShouldBeGreaterThan(0);
    }

    private static GcsObjectResource CreateResource(
        FakeGcsObjectClient client,
        string bucket = "bucket",
        string name = "settings.json",
        GcsObjectResourceOptions? options = null
    ) =>
        new(
            client,
            bucket,
            name,
            options ?? new GcsObjectResourceOptions
            {
                PollInterval = TimeSpan.FromMilliseconds(10),
            }
        );

    private static ConfiglueResourceContext CreateRoutedContext(string subject, string route)
    {
        var key = SubjectKey.From(subject);
        return new ConfiglueResourceContext(
            new ResourceSubject(subject),
            ResourceKey.From(key),
            RouteKey.From(route)
        );
    }

    private static byte[] Serialize(AppSettings.Fragment fragment)
    {
        var codec = new JsonStateCodec<AppSettings.Fragment>();
        var output = new ArrayBufferWriter<byte>();
        codec.Serialize(fragment, output, default);
        return output.WrittenSpan.ToArray();
    }

    private static byte[] Hash(byte[] content)
    {
        using var hash = SHA256.Create();
        return hash.ComputeHash(content);
    }

    private static byte[] CreateDeterministicPayload(int size)
    {
        var payload = new byte[size];
        for (var index = 0; index < payload.Length; index++)
        {
            payload[index] = (byte)((index * 37) % 251);
        }

        return payload;
    }

    private sealed record ResourceSubject(string Name) : IConfiglueSubject
    {
        public SubjectKey Key => SubjectKey.From(Name);
    }

    private sealed class StubStorageClient : StorageClient;

    private sealed class PrefixTransformer : IAsyncStateByteTransformer
    {
        private static readonly byte[] Prefix = Encoding.UTF8.GetBytes("GCS:");

        public ValueTask<ReadOnlyMemory<byte>> TransformReadAsync(
            ReadOnlyMemory<byte> source,
            CancellationToken cancellationToken = default
        )
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTaskCompat.FromResult(source.Slice(Prefix.Length));
        }

        public ValueTask<ReadOnlyMemory<byte>> TransformWriteAsync(
            ReadOnlyMemory<byte> source,
            CancellationToken cancellationToken = default
        )
        {
            cancellationToken.ThrowIfCancellationRequested();
            var prefixed = new byte[Prefix.Length + source.Length];
            Prefix.CopyTo(prefixed, 0);
            source.Span.CopyTo(prefixed.AsSpan(Prefix.Length));
            return ValueTaskCompat.FromResult<ReadOnlyMemory<byte>>(prefixed);
        }
    }

    private sealed record StoredObject(byte[] Content, GcsObjectMetadata Metadata);

    private sealed class FakeGcsObjectClient : IGcsObjectClient
    {
        private readonly object _gate = new();
        private readonly Dictionary<(string Bucket, string Name), StoredObject> _objects = new();
        private long _generationCounter = 1000;

        public int BufferedReadCallCount { get; private set; }

        public int MetadataCallCount { get; private set; }

        public int DownloadCallCount { get; private set; }

        public string? LastBucketName { get; private set; }

        public string? LastObjectName { get; private set; }

        public long? LastExpectedGeneration { get; private set; }

        public bool LastRequireMissing { get; private set; }

        public string? LastContentType { get; private set; }

        public ReadOnlyMemory<byte> LastContent { get; private set; }

        public GcsObjectReadResult Seed(
            string bucket,
            string name,
            byte[] content,
            string? contentType = "application/json",
            long? generation = null
        )
        {
            lock (_gate)
            {
                var assigned = generation ?? ++_generationCounter;
                var metadata = new GcsObjectMetadata(
                    assigned,
                    1,
                    $"etag-{assigned.ToString(CultureInfo.InvariantCulture)}",
                    DateTimeOffset.UtcNow,
                    contentType,
                    content.Length
                );
                _objects[(bucket, name)] = new StoredObject(content, metadata);
                return new GcsObjectReadResult(
                    content,
                    assigned.ToString(CultureInfo.InvariantCulture),
                    metadata
                );
            }
        }

        public void FlipGeneration(string bucket, string name, byte[]? content = null)
        {
            lock (_gate)
            {
                var current = _objects[(bucket, name)];
                var assigned = ++_generationCounter;
                _objects[(bucket, name)] = current with
                {
                    Content = content ?? current.Content,
                    Metadata = current.Metadata with
                    {
                        Generation = assigned,
                        Metageneration = current.Metadata.Metageneration + 1,
                        ETag =
                            $"etag-{assigned.ToString(CultureInfo.InvariantCulture)}",
                        Updated = DateTimeOffset.UtcNow,
                        Size = (content ?? current.Content).Length,
                    },
                };
            }
        }

        public void Delete(string bucket, string name)
        {
            lock (_gate)
            {
                _objects.Remove((bucket, name));
            }
        }

        public byte[] StoredContent(string bucket, string name)
        {
            lock (_gate)
            {
                return _objects[(bucket, name)].Content;
            }
        }

        public GcsObjectMetadata? StoredMetadata(string bucket, string name)
        {
            lock (_gate)
            {
                return _objects.TryGetValue((bucket, name), out var stored)
                    ? stored.Metadata
                    : null;
            }
        }

        public Task<GcsObjectReadResult?> GetObjectAsync(
            string bucketName,
            string objectName,
            CancellationToken cancellationToken
        )
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_gate)
            {
                BufferedReadCallCount++;
                LastBucketName = bucketName;
                LastObjectName = objectName;
                if (!_objects.TryGetValue((bucketName, objectName), out var stored))
                {
                    return Task.FromResult<GcsObjectReadResult?>(null);
                }

                return Task.FromResult<GcsObjectReadResult?>(
                    new(
                        stored.Content,
                        stored.Metadata.Generation?.ToString(CultureInfo.InvariantCulture),
                        stored.Metadata
                    )
                );
            }
        }

        public Task<GcsObjectMetadata?> GetMetadataAsync(
            string bucketName,
            string objectName,
            CancellationToken cancellationToken
        )
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_gate)
            {
                MetadataCallCount++;
                return Task.FromResult(
                    _objects.TryGetValue((bucketName, objectName), out var stored)
                        ? stored.Metadata
                        : null
                );
            }
        }

        public async Task DownloadObjectAsync(
            string bucketName,
            string objectName,
            Stream destination,
            CancellationToken cancellationToken
        )
        {
            byte[] content;
            lock (_gate)
            {
                DownloadCallCount++;
                if (!_objects.TryGetValue((bucketName, objectName), out var stored))
                {
                    throw new GcsObjectMissingException(bucketName, objectName);
                }

                content = stored.Content;
            }

            const int chunkSize = 65536;
            for (var offset = 0; offset < content.Length; offset += chunkSize)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await destination
                    .WriteAsync(
                        content,
                        offset,
                        Math.Min(chunkSize, content.Length - offset),
                        cancellationToken
                    )
                    .ConfigureAwait(false);
            }

            await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        public Task<GcsObjectWriteResult> PutObjectAsync(
            string bucketName,
            string objectName,
            ReadOnlyMemory<byte> content,
            long? expectedGeneration,
            bool requireMissing,
            string? contentType,
            CancellationToken cancellationToken
        )
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_gate)
            {
                LastBucketName = bucketName;
                LastObjectName = objectName;
                LastContent = content;
                LastExpectedGeneration = expectedGeneration;
                LastRequireMissing = requireMissing;
                LastContentType = contentType;
                var exists = _objects.TryGetValue((bucketName, objectName), out var current);
                if (
                    (requireMissing && exists)
                    || (
                        expectedGeneration.HasValue
                        && (!exists || current!.Metadata.Generation != expectedGeneration)
                    )
                )
                {
                    return Task.FromException<GcsObjectWriteResult>(
                        new GcsObjectConflictException(bucketName, objectName)
                    );
                }

                var assigned = ++_generationCounter;
                var stored = content.ToArray();
                var metadata = new GcsObjectMetadata(
                    assigned,
                    (current?.Metadata.Metageneration ?? 0) + 1,
                    $"etag-{assigned.ToString(CultureInfo.InvariantCulture)}",
                    DateTimeOffset.UtcNow,
                    contentType,
                    stored.Length
                );
                _objects[(bucketName, objectName)] = new StoredObject(stored, metadata);
                return Task.FromResult(
                    new GcsObjectWriteResult(
                        assigned.ToString(CultureInfo.InvariantCulture),
                        metadata
                    )
                );
            }
        }
    }
}
