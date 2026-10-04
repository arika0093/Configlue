using System.Buffers;
using Azure;
using Azure.Storage.Blobs;
using Configlue.Provider.Json;
using Configlue.Resource.AzureBlob;

namespace Configlue.Tests;

public sealed class AzureBlobResourceTests
{
    [Test]
    public async Task ReadAsync_ReturnsContentAndETagRevision()
    {
        var client = new FakeAzureBlobClient();
        client.Seed("container", "settings.json", [1, 2, 3]);
        var resource = new AzureBlobResource(client, "container", "settings.json");

        var result = await resource.ReadAsync();

        result.Status.ShouldBe(StateReadStatus.Success);
        result.Content.ToArray().ShouldBe(new byte[] { 1, 2, 3 });
        result.Revision.ShouldBe("\"etag-1\"");
        resource.IsPipelineReadPreferred.ShouldBeTrue();
    }

    [Test]
    public async Task ReadPipelineAsync_StreamsContentWithoutBufferedDownload()
    {
        var client = new FakeAzureBlobClient();
        client.Seed("container", "settings.json", [1, 2, 3]);
        var resource = new AzureBlobResource(client, "container", "settings.json");

        await using var result = await resource.ReadPipelineAsync();
        var content = await result.ReadAllAsync();
        var bytes = new byte[3];
        content.CopyTo(bytes);
        result.Content!.AdvanceTo(content.End);

        result.Status.ShouldBe(StateReadStatus.Success);
        bytes.ShouldBe(new byte[] { 1, 2, 3 });
        result.Revision.ShouldBe("\"etag-1\"");
        client.DownloadCount.ShouldBe(0);
        client.DownloadStreamingCount.ShouldBe(1);
    }

    [Test]
    public async Task ReadPipelineAsync_FallsBackToBufferedDownloadWithoutStreamClient()
    {
        var client = new FakeAzureBlobClient();
        client.Seed("container", "settings.json", [4, 5]);
        var resource = new AzureBlobResource(
            new BufferedOnlyBlobClient(client),
            "container",
            "settings.json"
        );

        await using var result = await resource.ReadPipelineAsync();
        var content = await result.ReadAllAsync();
        var bytes = content.ToArray();
        result.Content!.AdvanceTo(content.End);

        result.Status.ShouldBe(StateReadStatus.Success);
        bytes.ShouldBe(new byte[] { 4, 5 });
        result.Revision.ShouldBe("\"etag-1\"");
        client.DownloadCount.ShouldBe(1);
    }

    [Test]
    public async Task ReadAsync_MapsMissingBlobToNotFound()
    {
        var client = new FakeAzureBlobClient();
        var resource = new AzureBlobResource(client, "container", "missing.json");

        var read = await resource.ReadAsync();
        await using var pipeline = await resource.ReadPipelineAsync();

        read.Status.ShouldBe(StateReadStatus.NotFound);
        pipeline.Status.ShouldBe(StateReadStatus.NotFound);
    }

    [Test]
    public async Task ReadAsync_DoesNotHideMissingContainer()
    {
        var client = new MissingContainerBlobClient();
        var resource = new AzureBlobResource(client, "missing", "settings.json");

        await Should.ThrowAsync<RequestFailedException>(async () => await resource.ReadAsync());
    }

    [Test]
    public async Task WriteAsync_UsesExpectedETagAndMapsPreconditionFailures()
    {
        var client = new FakeAzureBlobClient();
        client.Seed("container", "settings.json", [1]);
        var resource = new AzureBlobResource(client, "container", "settings.json");

        var result = await resource.WriteAsync(
            new ResourceWriteRequest(
                new byte[] { 4, 5 },
                Condition: RevisionCondition.FromRevision("\"etag-1\"")
            )
        );

        client.LastExpectedETag.ShouldBe("\"etag-1\"");
        client.LastRequireMissing.ShouldBeFalse();
        client.LastContent.ShouldBe(new byte[] { 4, 5 });
        result.Revision.ShouldBe("\"etag-2\"");
        (await resource.ReadAsync()).Revision.ShouldBe("\"etag-2\"");

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
    public async Task WriteAsync_MapsRequireMissingRaceToConflict()
    {
        var client = new FakeAzureBlobClient();
        client.Seed("container", "settings.json", [1]);
        var resource = new AzureBlobResource(client, "container", "settings.json");

        var conflict = await Should.ThrowAsync<StateConflictException>(async () =>
            await resource.WriteAsync(
                new ResourceWriteRequest(new byte[] { 2 }, Condition: RevisionCondition.MustNotExist)
            )
        );

        conflict.Message.ShouldContain("container/settings.json");
    }

    [Test]
    public async Task WriteAsync_CreatesMissingBlobOnlyWhenRequired()
    {
        var client = new FakeAzureBlobClient();
        var resource = new AzureBlobResource(client, "container", "settings.json");

        var created = await resource.WriteAsync(
            new ResourceWriteRequest(new byte[] { 1 }, Condition: RevisionCondition.MustNotExist)
        );

        client.LastExpectedETag.ShouldBeNull();
        client.LastRequireMissing.ShouldBeTrue();
        created.Revision.ShouldBe("\"etag-1\"");

        await Should.ThrowAsync<StateConflictException>(async () =>
            await resource.WriteAsync(
                new ResourceWriteRequest(new byte[] { 2 }, Condition: RevisionCondition.MustNotExist)
            )
        );
        (await resource.ReadAsync()).Content.ToArray().ShouldBe(new byte[] { 1 });
    }

    [Test]
    public async Task WriteAsync_IsUnconditionalUnlessRevisionCheckIsRequested()
    {
        var client = new FakeAzureBlobClient();
        client.Seed("container", "settings.json", [1]);
        var resource = new AzureBlobResource(client, "container", "settings.json");

        var result = await resource.WriteAsync(new ResourceWriteRequest(new byte[] { 9 }));

        client.LastExpectedETag.ShouldBeNull();
        client.LastRequireMissing.ShouldBeFalse();
        result.Revision.ShouldBe("\"etag-2\"");
        (await resource.ReadAsync()).Content.ToArray().ShouldBe(new byte[] { 9 });
    }

    [Test]
    public async Task WriteAsync_PreservesContentTypeAndMetadata()
    {
        var client = new FakeAzureBlobClient();
        var resource = new AzureBlobResource(
            client,
            "container",
            "settings.json",
            new AzureBlobResourceOptions
            {
                ContentType = "application/json",
                Metadata = new Dictionary<string, string> { ["owner"] = "configlue" },
            }
        );

        await resource.WriteAsync(new ResourceWriteRequest(new byte[] { 1 }));

        client.LastContentType.ShouldBe("application/json");
        client.LastMetadata.ShouldBe(new Dictionary<string, string> { ["owner"] = "configlue" });
        var properties = await client.GetPropertiesAsync(
            "container",
            "settings.json",
            CancellationToken.None
        );
        properties!.ContentType.ShouldBe("application/json");

        var unconditional = new AzureBlobResource(client, "container", "settings.json");
        await unconditional.WriteAsync(new ResourceWriteRequest(new byte[] { 2 }));

        (await client.GetPropertiesAsync("container", "settings.json", CancellationToken.None))!
            .ContentType.ShouldBe("application/json");
    }

    [Test]
    public async Task WriteAsync_PreservesFullProvenanceMetadata()
    {
        var client = new FakeAzureBlobClient();
        var resource = new AzureBlobResource(
            client,
            "container",
            "settings.json",
            new AzureBlobResourceOptions { ContentType = "application/json" }
        );

        var written = await resource.WriteAsync(
            new ResourceWriteRequest(new byte[] { 7, 8, 9 })
        );
        var properties = await client.GetPropertiesAsync(
            "container",
            "settings.json",
            CancellationToken.None
        );

        properties!.ETag.ShouldBe(written.Revision);
        properties.ContentLength.ShouldBe(3);
        properties.ContentType.ShouldBe("application/json");
        properties.VersionId.ShouldNotBeNullOrWhiteSpace();
        (properties.LastModified <= DateTimeOffset.UtcNow).ShouldBeTrue();
    }

    [Test]
    public async Task SubjectAwareAddressSelectsBlobAndKeepsItsETagCondition()
    {
        var client = new FakeAzureBlobClient();
        var resource = new AzureBlobResource(
            client,
            "settings-default",
            "settings.json",
            new AzureBlobResourceOptions
            {
                ContainerNameSelector = context => $"settings-{context.Route.Value}",
                BlobNameSelector = context => $"{context.ResourceKey.Value}/settings.json",
            }
        );
        var subject = new ResourceSubject("tenant-a");
        var context = new ConfiglueResourceContext(
            subject,
            ResourceKey.From(subject.Key),
            RouteKey.From("region-jp")
        );

        await resource.WriteAsync(
            context,
            new ResourceWriteRequest(new byte[] { 1, 2 }, Condition: RevisionCondition.MustNotExist)
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
        client.LastContainerName.ShouldBe("settings-region-jp");
        client.LastBlobName.ShouldBe($"{subject.Key.Value}/settings.json");
        client.LastExpectedETag.ShouldBe(read.Revision);
        write.Revision.ShouldNotBeNullOrWhiteSpace();
        var defaultResource = new AzureBlobResource(
            new FakeAzureBlobClient(),
            "settings-default",
            "settings.json"
        );
        resource
            .GetResourceId(context)
            .ShouldNotBe(defaultResource.GetResourceId(ConfiglueResourceContext.Default));
    }

    [Test]
    public async Task InjectedClientSelectorRoutesEachContextToItsOwnBlob()
    {
        var first = new FakeAzureBlobClient();
        var second = new FakeAzureBlobClient();
        first.Seed("first-container", "first.json", [10]);
        second.Seed("second-container", "second.json", [20]);
        var fallback = new FakeAzureBlobClient();
        var resource = new AzureBlobResource(
            fallback,
            "fallback-container",
            "fallback.json",
            options: null,
            bindingSelector: context =>
                context.Route.Value == "first"
                    ? new AzureBlobBinding(first, "first-container", "first.json", "first")
                    : new AzureBlobBinding(second, "second-container", "second.json", "second")
        );

        var firstContext = new ConfiglueResourceContext(
            new ResourceSubject("tenant"),
            ResourceKey.From(SubjectKey.From("tenant")),
            RouteKey.From("first")
        );
        var secondContext = new ConfiglueResourceContext(
            new ResourceSubject("tenant"),
            ResourceKey.From(SubjectKey.From("tenant")),
            RouteKey.From("second")
        );

        (await resource.ReadAsync(firstContext)).Content.ToArray().ShouldBe([10]);
        (await resource.ReadAsync(secondContext)).Content.ToArray().ShouldBe([20]);
        fallback.DownloadCount.ShouldBe(0);
        resource
            .GetResourceId(firstContext)
            .ShouldNotBe(resource.GetResourceId(secondContext));
    }

    [Test]
    public void BlobClientSelectorCannotCombineWithAddressSelectors()
    {
        var client = new BlobClient(
            "UseDevelopmentStorage=true",
            "container",
            "settings.json"
        );

        Should.Throw<ArgumentException>(() =>
            new AzureBlobResource(
                new BlobServiceClient("UseDevelopmentStorage=true"),
                "container",
                "settings.json",
                new AzureBlobResourceOptions
                {
                    ContainerNameSelector = static _ => "other",
                    BlobClientSelector = _ => client,
                }
            )
        );
    }

    [Test]
    public void InjectedSdkClientsDetermineContainerAndBlob()
    {
        var service = new AzureBlobResource(
            new BlobServiceClient("UseDevelopmentStorage=true"),
            "container",
            "settings.json"
        );
        var container = new AzureBlobResource(
            new BlobContainerClient("UseDevelopmentStorage=true", "container"),
            "settings.json"
        );
        var blob = new AzureBlobResource(
            new BlobClient("UseDevelopmentStorage=true", "container", "settings.json")
        );

        service.ContainerName.ShouldBe("container");
        service.BlobName.ShouldBe("settings.json");
        container.ContainerName.ShouldBe("container");
        blob.ContainerName.ShouldBe("container");
        blob.BlobName.ShouldBe("settings.json");
        var context = ConfiglueResourceContext.Default;
        container.GetResourceId(context).ShouldBe(service.GetResourceId(context));
        blob.GetResourceId(context).ShouldBe(service.GetResourceId(context));
    }

    [Test]
    public void ResourceId_IsStableForContainerAndBlobAndCanBeOverridden()
    {
        var first = new AzureBlobResource(new FakeAzureBlobClient(), "container", "settings.json");
        var same = new AzureBlobResource(new FakeAzureBlobClient(), "container", "settings.json");
        var different = new AzureBlobResource(new FakeAzureBlobClient(), "container", "other.json");
        var overridden = new ResourceId("deployment:settings");
        using var withOverride = new AzureBlobResource(
            new FakeAzureBlobClient(),
            "container",
            "settings.json",
            new AzureBlobResourceOptions { FixedResourceId = overridden }
        );

        var context = ConfiglueResourceContext.Default;
        same.GetResourceId(context).ShouldBe(first.GetResourceId(context));
        different.GetResourceId(context).ShouldNotBe(first.GetResourceId(context));
        withOverride.GetResourceId(context).ShouldBe(overridden);
    }

    [Test]
    public async Task WaitForChangeAsync_ReturnsImmediatelyWhenRevisionDiffers()
    {
        var client = new FakeAzureBlobClient();
        client.Seed("container", "settings.json", [1]);
        using var resource = new AzureBlobResource(
            client,
            "container",
            "settings.json",
            new AzureBlobResourceOptions
            {
                EnableWatching = true,
                WatchPollInterval = TimeSpan.FromMilliseconds(20),
            }
        );

        await resource.WaitForChangeAsync(ConfiglueResourceContext.Default, "\"stale\"");

        client.GetPropertiesCount.ShouldBe(1);
        client.DownloadCount.ShouldBe(0);
        client.DownloadStreamingCount.ShouldBe(0);
    }

    [Test]
    public async Task WaitForChangeAsync_PollsMetadataUntilBlobChanges()
    {
        var client = new FakeAzureBlobClient();
        client.Seed("container", "settings.json", [1]);
        using var resource = new AzureBlobResource(
            client,
            "container",
            "settings.json",
            new AzureBlobResourceOptions
            {
                EnableWatching = true,
                WatchPollInterval = TimeSpan.FromMilliseconds(20),
            }
        );
        var observed = (
            await client.GetPropertiesAsync("container", "settings.json", CancellationToken.None)
        )!.ETag;

        var updater = Task.Run(async () =>
        {
            await Task.Delay(150);
            await resource.WriteAsync(new ResourceWriteRequest(new byte[] { 2 }));
        });
        await resource.WaitForChangeAsync(
            ConfiglueResourceContext.Default,
            observed,
            CancellationToken.None
        );
        await updater;

        (client.GetPropertiesCount >= 2).ShouldBeTrue();
        client.DownloadCount.ShouldBe(0);
        client.DownloadStreamingCount.ShouldBe(0);
        (await resource.ReadAsync()).Content.ToArray().ShouldBe(new byte[] { 2 });
    }

    [Test]
    public async Task WaitForChangeAsync_WaitsForMissingBlobToAppear()
    {
        var client = new FakeAzureBlobClient();
        using var resource = new AzureBlobResource(
            client,
            "container",
            "settings.json",
            new AzureBlobResourceOptions
            {
                EnableWatching = true,
                WatchPollInterval = TimeSpan.FromMilliseconds(20),
            }
        );

        var creator = Task.Run(async () =>
        {
            await Task.Delay(120);
            await resource.WriteAsync(
                new ResourceWriteRequest(new byte[] { 5 }, Condition: RevisionCondition.MustNotExist)
            );
        });
        await resource.WaitForChangeAsync(
            ConfiglueResourceContext.Default,
            observedRevision: null,
            CancellationToken.None
        );
        await creator;

        client.DownloadCount.ShouldBe(0);
        client.DownloadStreamingCount.ShouldBe(0);
        (await resource.ReadAsync()).Content.ToArray().ShouldBe(new byte[] { 5 });
    }

    [Test]
    public async Task WaitForChangeAsync_ThrowsWhenWatchingIsDisabled()
    {
        using var resource = new AzureBlobResource(
            new FakeAzureBlobClient(),
            "container",
            "settings.json"
        );

        await Should.ThrowAsync<InvalidOperationException>(async () =>
            await resource.WaitForChangeAsync(ConfiglueResourceContext.Default, null)
        );
    }

    [Test]
    public async Task WaitForChangeAsync_HonorsCancellationWhilePolling()
    {
        var client = new FakeAzureBlobClient();
        client.Seed("container", "settings.json", [1]);
        using var resource = new AzureBlobResource(
            client,
            "container",
            "settings.json",
            new AzureBlobResourceOptions
            {
                EnableWatching = true,
                WatchPollInterval = TimeSpan.FromMilliseconds(20),
            }
        );
        var observed = (
            await client.GetPropertiesAsync("container", "settings.json", CancellationToken.None)
        )!.ETag;
        using var cancellation = new CancellationTokenSource();
        cancellation.CancelAfter(120);

        await Should.ThrowAsync<OperationCanceledException>(async () =>
            await resource.WaitForChangeAsync(
                ConfiglueResourceContext.Default,
                observed,
                cancellation.Token
            )
        );
        client.DownloadCount.ShouldBe(0);
    }

    [Test]
    public async Task WaitForChangeAsync_WakesWhenDisposed()
    {
        var client = new FakeAzureBlobClient();
        client.Seed("container", "settings.json", [1]);
        var resource = new AzureBlobResource(
            client,
            "container",
            "settings.json",
            new AzureBlobResourceOptions
            {
                EnableWatching = true,
                WatchPollInterval = TimeSpan.FromSeconds(30),
            }
        );
        var observed = (await resource.ReadAsync()).Revision;
        var waiter = resource
            .WaitForChangeAsync(ConfiglueResourceContext.Default, observed)
            .AsTask();
        await Task.Delay(150);
        resource.Dispose();

        await Should.ThrowAsync<OperationCanceledException>(async () => await waiter);
        resource.Dispose();
        await Should.ThrowAsync<ObjectDisposedException>(async () => await resource.ReadAsync());
        await Should.ThrowAsync<ObjectDisposedException>(async () =>
            await resource.WriteAsync(new ResourceWriteRequest(new byte[] { 1 }))
        );
        await Should.ThrowAsync<ObjectDisposedException>(async () =>
            await resource.WaitForChangeAsync(ConfiglueResourceContext.Default, observed)
        );
    }

    [Test]
    public async Task Operations_HonorPreCanceledTokens()
    {
        var client = new FakeAzureBlobClient();
        client.Seed("container", "settings.json", [1]);
        using var resource = new AzureBlobResource(
            client,
            "container",
            "settings.json",
            new AzureBlobResourceOptions
            {
                EnableWatching = true,
                WatchPollInterval = TimeSpan.FromMilliseconds(20),
            }
        );
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();

        await Should.ThrowAsync<OperationCanceledException>(async () =>
            await resource.ReadAsync(ConfiglueResourceContext.Default, canceled.Token)
        );
        await Should.ThrowAsync<OperationCanceledException>(async () =>
            await resource.WriteAsync(
                new ResourceWriteRequest(new byte[] { 2 }),
                canceled.Token
            )
        );
        await Should.ThrowAsync<OperationCanceledException>(async () =>
            await resource.WaitForChangeAsync(
                ConfiglueResourceContext.Default,
                "\"etag-1\"",
                canceled.Token
            )
        );
    }

    [Test]
    public async Task LargePayloads_RoundTripThroughReadsAndPipeline()
    {
        var client = new FakeAzureBlobClient();
        var resource = new AzureBlobResource(client, "container", "large.bin");
        var payload = new byte[2 * 1024 * 1024];
        for (var index = 0; index < payload.Length; index++)
        {
            payload[index] = (byte)(index % 251);
        }

        var written = await resource.WriteAsync(new ResourceWriteRequest(payload));
        written.Revision.ShouldNotBeNullOrWhiteSpace();

        var read = await resource.ReadAsync();
        read.Content.ToArray().ShouldBe(payload);
        var downloadsBeforePipeline = client.DownloadCount;

        await using var pipeline = await resource.ReadPipelineAsync();
        var sequence = await pipeline.ReadAllAsync();
        sequence.Length.ShouldBe(payload.Length);
        sequence.FirstSpan[0].ShouldBe(payload[0]);
        var streamed = sequence.ToArray();
        pipeline.Content!.AdvanceTo(sequence.End);
        streamed.ShouldBe(payload);
        client.DownloadCount.ShouldBe(downloadsBeforePipeline);
        client.DownloadStreamingCount.ShouldBe(1);
    }

    [Test]
    public async Task SlicedBuffers_UploadWithoutReshapingContent()
    {
        var client = new FakeAzureBlobClient();
        var resource = new AzureBlobResource(client, "container", "slice.bin");
        var backing = new byte[] { 9, 8, 1, 2, 3, 7, 7 };
        var sliced = new ReadOnlyMemory<byte>(backing, 2, 3);

        await resource.WriteAsync(new ResourceWriteRequest(sliced));

        client.LastContent.ShouldBe(new byte[] { 1, 2, 3 });
        (await resource.ReadAsync()).Content.ToArray().ShouldBe(new byte[] { 1, 2, 3 });
    }

    [Test]
    public async Task TransformerAndCodec_ComposeThroughSerializedSource()
    {
        var client = new FakeAzureBlobClient();
        var resource = new AzureBlobResource(client, "container", "settings.json");
        var codec = new JsonStateCodec<AppSettings.Fragment>();
        var source = SerializedStateSource.FromResource<AppSettings.Fragment>(
            "blob",
            resource,
            codec,
            writer: resource,
            transformers: [new XorTransformer(0x5A)]
        );
        var fragment = new AppSettings.Fragment
        {
            RetryCount = Optional<int>.Present(7),
            Label = Optional<string?>.Present("hello"),
        };

        await source.Writer!.WriteAsync(
            ConfiglueResourceContext.Default,
            new StateWriteRequest<AppSettings.Fragment>(fragment)
        );
        var stored = await resource.ReadAsync(ConfiglueResourceContext.Default);

        stored.Status.ShouldBe(StateReadStatus.Success);
        stored.Content.Span.IndexOf("hello"u8).ShouldBe(-1);
        var read = await source.Reader.ReadAsync(ConfiglueResourceContext.Default);

        read.Status.ShouldBe(StateReadStatus.Success);
        read.Value!.RetryCount.Value.ShouldBe(7);
        read.Value!.Label.Value.ShouldBe("hello");
    }

    [Test]
    public async Task ConflictMessages_NeverExposeSecrets()
    {
        var client = new FakeAzureBlobClient();
        var resource = new AzureBlobResource(client, "container", "settings.json");

        var conflict = await Should.ThrowAsync<StateConflictException>(async () =>
            await resource.WriteAsync(
                new ResourceWriteRequest(
                    new byte[] { 1 },
                    Condition: RevisionCondition.FromRevision("\"stale\"")
                )
            )
        );

        conflict.Message.ShouldNotContain("sig=");
        conflict.Message.ShouldContain("container/settings.json");
    }

    [Test]
    public void SourceRegistration_RequiresExactlyOneCredential()
    {
        var codec = StateCodecBinding.Typed(new JsonStateCodec<AppSettings.Fragment>());
        var client = new BlobClient("UseDevelopmentStorage=true", "container", "settings.json");
        var builder = new ConfiglueSourceSetBuilder();

        var both = new AzureBlobSourceOptions
        {
            ContainerName = "container",
            BlobName = "settings.json",
            Client = client,
            ConnectionString = "AccountName=dev;AccountKey=c2VjcmV0;",
            Codec = codec,
        };
        var failure = Should.Throw<ArgumentException>(() => builder.FromAzureBlob(both));
        failure.Message.ShouldNotContain("c2VjcmV0");

        var endpointOnly = new AzureBlobSourceOptions
        {
            ContainerName = "container",
            BlobName = "settings.json",
            ServiceEndpoint = new Uri("https://account.blob.core.windows.net"),
            Codec = codec,
        };
        Should.Throw<ArgumentException>(() => builder.FromAzureBlob(endpointOnly));
    }

    [Test]
    public void SourceRegistration_RejectsClientTargetingAnotherBlob()
    {
        var codec = StateCodecBinding.Typed(new JsonStateCodec<AppSettings.Fragment>());
        var builder = new ConfiglueSourceSetBuilder();
        var options = new AzureBlobSourceOptions
        {
            ContainerName = "container",
            BlobName = "expected.json",
            Client = new BlobClient("UseDevelopmentStorage=true", "container", "other.json"),
            Codec = codec,
        };

        Should.Throw<ArgumentException>(() => builder.FromAzureBlob(options));
    }

    [Test]
    public async Task SourceRegistration_ExposesContainerOriginWithoutSecrets()
    {
        await using var context = ConfiglueApp.CreateContext(builder =>
        {
            builder.Add<AppSettings>(model =>
                model.Sources(sources =>
                {
                    sources.FromAzureBlob(
                        new AzureBlobSourceOptions
                        {
                            ContainerName = "settings",
                            BlobName = "one.json",
                            Client = new BlobClient(
                                "UseDevelopmentStorage=true",
                                "settings",
                                "one.json"
                            ),
                            Codec = StateCodecBinding.Typed(
                                new JsonStateCodec<AppSettings.Fragment>()
                            ),
                            Writable = false,
                        }
                    );
                    sources.FromAzureBlob(
                        new AzureBlobSourceOptions
                        {
                            ContainerName = "settings",
                            BlobName = "two.json",
                            Client = new BlobClient(
                                "UseDevelopmentStorage=true",
                                "settings",
                                "two.json"
                            ),
                            Codec = StateCodecBinding.Typed(
                                new JsonStateCodec<AppSettings.Fragment>()
                            ),
                            Writable = false,
                        }
                    );
                })
            );
        });

        var sources = context.GetRuntimeState<AppSettings>().GetDiagnostics().Sources;
        sources.Count.ShouldBe(2);
        sources.Select(static source => source.Id).Distinct().Count().ShouldBe(2);
        sources
            .All(static source => source.PhysicalOrigin == "azureblob:settings")
            .ShouldBeTrue();
    }

    private sealed class FakeAzureBlobClient : IAzureBlobClient, IAzureBlobStreamClient
    {
        private readonly object _gate = new();
        private readonly Dictionary<(string Container, string Blob), BlobEntry> _store = new();
        private long _version;

        public int DownloadCount { get; private set; }

        public int DownloadStreamingCount { get; private set; }

        public int GetPropertiesCount { get; private set; }

        public int UploadCount { get; private set; }

        public string? LastContainerName { get; private set; }

        public string? LastBlobName { get; private set; }

        public byte[] LastContent { get; private set; } = [];

        public string? LastExpectedETag { get; private set; }

        public bool LastRequireMissing { get; private set; }

        public string? LastContentType { get; private set; }

        public IDictionary<string, string>? LastMetadata { get; private set; }

        public void Seed(string containerName, string blobName, byte[] content)
        {
            lock (_gate)
            {
                var version = ++_version;
                _store[(containerName, blobName)] = new BlobEntry(
                    (byte[])content.Clone(),
                    $"\"etag-{version}\"",
                    DateTimeOffset.UtcNow,
                    content.Length,
                    "application/octet-stream",
                    $"version-{version}",
                    new Dictionary<string, string>()
                );
            }
        }

        public Task<AzureBlobReadResult> DownloadAsync(
            string containerName,
            string blobName,
            CancellationToken cancellationToken
        )
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_gate)
            {
                DownloadCount++;
                CaptureAddress(containerName, blobName);
                var entry = Require(containerName, blobName);
                return Task.FromResult(
                    new AzureBlobReadResult(
                        (byte[])entry.Content.Clone(),
                        entry.ETag,
                        entry.LastModified,
                        entry.ContentLength,
                        entry.ContentType,
                        entry.VersionId
                    )
                );
            }
        }

        public Task<AzureBlobStreamResult> DownloadStreamingAsync(
            string containerName,
            string blobName,
            CancellationToken cancellationToken
        )
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_gate)
            {
                DownloadStreamingCount++;
                CaptureAddress(containerName, blobName);
                var entry = Require(containerName, blobName);
                return Task.FromResult(
                    new AzureBlobStreamResult(
                        new MemoryStream((byte[])entry.Content.Clone(), writable: false),
                        entry.ETag
                    )
                );
            }
        }

        public Task<AzureBlobPropertiesResult?> GetPropertiesAsync(
            string containerName,
            string blobName,
            CancellationToken cancellationToken
        )
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_gate)
            {
                GetPropertiesCount++;
                CaptureAddress(containerName, blobName);
                var entry = Require(containerName, blobName);
                return Task.FromResult<AzureBlobPropertiesResult?>(
                    new AzureBlobPropertiesResult(
                        entry.ETag,
                        entry.LastModified,
                        entry.ContentLength,
                        entry.ContentType,
                        entry.VersionId
                    )
                );
            }
        }

        public Task<AzureBlobWriteResult> UploadAsync(
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
            cancellationToken.ThrowIfCancellationRequested();
            lock (_gate)
            {
                UploadCount++;
                CaptureAddress(containerName, blobName);
                LastContent = content.ToArray();
                LastExpectedETag = expectedETag;
                LastRequireMissing = requireMissing;
                LastContentType = contentType;
                LastMetadata = metadata is null
                    ? null
                    : new Dictionary<string, string>(metadata);
                var key = (containerName, blobName);
                var exists = _store.TryGetValue(key, out var current);
                if (requireMissing && exists)
                {
                    throw new RequestFailedException(
                        409,
                        "The specified blob already exists.",
                        "BlobAlreadyExists",
                        null
                    );
                }

                if (
                    expectedETag is not null
                    && (
                        !exists
                        || !string.Equals(current!.ETag, expectedETag, StringComparison.Ordinal)
                    )
                )
                {
                    throw new RequestFailedException(
                        412,
                        "The condition specified using HTTP conditional header(s) is not met.",
                        "ConditionNotMet",
                        null
                    );
                }

                var version = ++_version;
                var entry = new BlobEntry(
                    content.ToArray(),
                    $"\"etag-{version}\"",
                    DateTimeOffset.UtcNow,
                    content.Length,
                    contentType ?? current?.ContentType,
                    $"version-{version}",
                    metadata is null ? new Dictionary<string, string>() : new(metadata)
                );
                _store[key] = entry;
                return Task.FromResult(
                    new AzureBlobWriteResult(entry.ETag, entry.LastModified, entry.VersionId)
                );
            }
        }

        private BlobEntry Require(string containerName, string blobName)
        {
            if (_store.TryGetValue((containerName, blobName), out var entry))
            {
                return entry;
            }

            throw new RequestFailedException(
                404,
                "The specified blob does not exist.",
                "BlobNotFound",
                null
            );
        }

        private void CaptureAddress(string containerName, string blobName)
        {
            LastContainerName = containerName;
            LastBlobName = blobName;
        }

        private sealed record BlobEntry(
            byte[] Content,
            string ETag,
            DateTimeOffset LastModified,
            long ContentLength,
            string? ContentType,
            string VersionId,
            IDictionary<string, string> Metadata
        );
    }

    private sealed class BufferedOnlyBlobClient(FakeAzureBlobClient inner) : IAzureBlobClient
    {
        public Task<AzureBlobReadResult> DownloadAsync(
            string containerName,
            string blobName,
            CancellationToken cancellationToken
        ) => inner.DownloadAsync(containerName, blobName, cancellationToken);

        public Task<AzureBlobPropertiesResult?> GetPropertiesAsync(
            string containerName,
            string blobName,
            CancellationToken cancellationToken
        ) => inner.GetPropertiesAsync(containerName, blobName, cancellationToken);

        public Task<AzureBlobWriteResult> UploadAsync(
            string containerName,
            string blobName,
            ReadOnlyMemory<byte> content,
            string? expectedETag,
            bool requireMissing,
            string? contentType,
            IDictionary<string, string>? metadata,
            CancellationToken cancellationToken
        ) =>
            inner.UploadAsync(
                containerName,
                blobName,
                content,
                expectedETag,
                requireMissing,
                contentType,
                metadata,
                cancellationToken
            );
    }

    private sealed class MissingContainerBlobClient : IAzureBlobClient
    {
        public Task<AzureBlobReadResult> DownloadAsync(
            string containerName,
            string blobName,
            CancellationToken cancellationToken
        ) =>
            Task.FromException<AzureBlobReadResult>(
                new RequestFailedException(
                    404,
                    "The specified container does not exist.",
                    "ContainerNotFound",
                    null
                )
            );

        public Task<AzureBlobPropertiesResult?> GetPropertiesAsync(
            string containerName,
            string blobName,
            CancellationToken cancellationToken
        ) =>
            Task.FromException<AzureBlobPropertiesResult?>(
                new RequestFailedException(
                    404,
                    "The specified container does not exist.",
                    "ContainerNotFound",
                    null
                )
            );

        public Task<AzureBlobWriteResult> UploadAsync(
            string containerName,
            string blobName,
            ReadOnlyMemory<byte> content,
            string? expectedETag,
            bool requireMissing,
            string? contentType,
            IDictionary<string, string>? metadata,
            CancellationToken cancellationToken
        ) =>
            Task.FromException<AzureBlobWriteResult>(
                new RequestFailedException(
                    404,
                    "The specified container does not exist.",
                    "ContainerNotFound",
                    null
                )
            );
    }

    private sealed class XorTransformer : IAsyncStateByteTransformer
    {
        private readonly byte _key;

        public XorTransformer(byte key)
        {
            _key = key;
        }

        public ValueTask<ReadOnlyMemory<byte>> TransformReadAsync(
            ReadOnlyMemory<byte> source,
            CancellationToken cancellationToken = default
        )
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTaskCompat.FromResult<ReadOnlyMemory<byte>>(Transform(source));
        }

        public ValueTask<ReadOnlyMemory<byte>> TransformWriteAsync(
            ReadOnlyMemory<byte> source,
            CancellationToken cancellationToken = default
        )
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTaskCompat.FromResult<ReadOnlyMemory<byte>>(Transform(source));
        }

        private ReadOnlyMemory<byte> Transform(ReadOnlyMemory<byte> source)
        {
            var output = new byte[source.Length];
            var span = source.Span;
            for (var index = 0; index < span.Length; index++)
            {
                output[index] = (byte)(span[index] ^ _key);
            }

            return output;
        }
    }

    private sealed record ResourceSubject(string Name) : IConfiglueSubject
    {
        public SubjectKey Key => SubjectKey.From(Name);
    }
}
