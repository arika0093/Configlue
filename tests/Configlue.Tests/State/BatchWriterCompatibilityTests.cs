using System.Security.Cryptography;
using Configlue.Provider.Json;
using Configlue.Testing;

namespace Configlue.Tests;

public sealed class BatchWriterCompatibilityTests
{
    [Test]
    public async Task SameResourceIdWithCompatibleWriterDomainBatchesOnce()
    {
        var store = new SharedBatchStore("memory:compatible-domain");
        var first = new CompatibleSharedBatchResource(store, "domain");
        var second = new CompatibleSharedBatchResource(store, "domain");
        var runtime = BuildRuntime(first, second, "App:First", "App:Second", "first");

        var result = await runtime.ApplyPatchesAsync([
            new StateSourcePatch(
                "first",
                new AppSettings.Patch { RetryCount = FragmentOperation<int>.Set(7) }
            ),
            new StateSourcePatch(
                "second",
                new AppSettings.Patch { Label = FragmentOperation<string?>.Set("second") }
            ),
        ]);

        result.PhysicalWriteCount.ShouldBe(1);
        store.WriteCount.ShouldBe(1);
    }

    [Test]
    public async Task EquivalentFileResourceInstancesForOnePathBatchOnce()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"configlue-compat-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "settings.json");
        var first = new FileResource(path);
        var second = new FileResource(path);
        try
        {
            first.ResourceId.ShouldBe(second.ResourceId);
            var runtime = BuildRuntime(first, second, "App:First", "App:Second", "first");

            var result = await runtime.ApplyPatchesAsync([
                new StateSourcePatch(
                    "first",
                    new AppSettings.Patch { RetryCount = FragmentOperation<int>.Set(3) }
                ),
                new StateSourcePatch(
                    "second",
                    new AppSettings.Patch { Label = FragmentOperation<string?>.Set("file") }
                ),
            ]);

            result.PhysicalWriteCount.ShouldBe(1);
        }
        finally
        {
            first.Dispose();
            second.Dispose();
            Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    public async Task SameResourceIdWithIncompatibleWritersIsRejectedBeforeWriting()
    {
        var store = new SharedBatchStore("memory:incompatible-domain");
        var first = new SharedBatchResource(store);
        var second = new SharedBatchResource(store);
        var runtime = BuildRuntime(first, second, "App:First", "App:Second", "first");

        await Should.ThrowAsync<NotSupportedException>(async () =>
            await runtime.ApplyPatchesAsync([
                new StateSourcePatch(
                    "first",
                    new AppSettings.Patch { RetryCount = FragmentOperation<int>.Set(7) }
                ),
                new StateSourcePatch(
                    "second",
                    new AppSettings.Patch { Label = FragmentOperation<string?>.Set("second") }
                ),
            ])
        );

        store.WriteCount.ShouldBe(0);
    }

    [Test]
    public async Task DifferentTransformationPipelinesAreRejectedBeforeWriting()
    {
        var physical = new InMemoryResource();
        var first = new TransformingResource(physical, [new PassthroughTransformer()]);
        var second = new TransformingResource(physical, [new PassthroughTransformer()]);
        var runtime = BuildRuntime(
            new JsonSectionResource(first, first.Writer!, "App:First"),
            new JsonSectionResource(second, second.Writer!, "App:Second"),
            "first"
        );

        await Should.ThrowAsync<NotSupportedException>(async () =>
            await runtime.ApplyPatchesAsync([
                new StateSourcePatch(
                    "first",
                    new AppSettings.Patch { RetryCount = FragmentOperation<int>.Set(7) }
                ),
                new StateSourcePatch(
                    "second",
                    new AppSettings.Patch { Label = FragmentOperation<string?>.Set("second") }
                ),
            ])
        );

        physical.WriteCount.ShouldBe(0);
    }

    [Test]
    public async Task EquivalentTransformationWrappersOptInAndBatchOnce()
    {
        var physical = new InMemoryResource();
        var transformer = new PassthroughTransformer();
        var first = new TransformingResource(physical, [transformer]);
        var second = new TransformingResource(physical, [transformer]);
        var runtime = BuildRuntime(
            new JsonSectionResource(first, first.Writer!, "App:First"),
            new JsonSectionResource(second, second.Writer!, "App:Second"),
            "first"
        );

        var result = await runtime.ApplyPatchesAsync([
            new StateSourcePatch(
                "first",
                new AppSettings.Patch { RetryCount = FragmentOperation<int>.Set(9) }
            ),
            new StateSourcePatch(
                "second",
                new AppSettings.Patch { Label = FragmentOperation<string?>.Set("wrapped") }
            ),
        ]);

        result.PhysicalWriteCount.ShouldBe(1);
        physical.WriteCount.ShouldBe(1);
    }

    [Test]
    public async Task MixedSyncAndAsyncParticipantsBatchOneCompatibleWriter()
    {
        var resource = new InMemoryResource();
        var codec = new JsonStateCodec<AppSettings.Fragment>();
        var firstSection = new JsonSectionResource(resource, "App:First");
        var secondSection = new JsonSectionResource(resource, "App:Second");
        var syncSource = SerializedStateSource.FromResource<AppSettings.Fragment>(
            "sync",
            firstSection,
            codec,
            priority: 10
        );
        var asyncBase = new StateSource<AppSettings.Fragment>(
            "async-base",
            new SerializedStateReader<AppSettings.Fragment>(secondSection, codec),
            writer: new SerializedStateWriter<AppSettings.Fragment>(secondSection, codec)
        );
        var asyncSource = StateSourceProjection.ProjectWithUpdate<
            AppSettings.Fragment,
            AppSettings.Fragment
        >(
            asyncBase,
            static fragment => fragment,
            static (_, next, _) => next,
            AppSettings.ConfiglueSchema.ToMetadata()
        );
        var runtime = new ConfiglueRuntime<AppSettings, AppSettings.Fragment>(
            new StateSourceSet<AppSettings.Fragment>([syncSource, asyncSource]),
            StateWritePlan.DefaultTo("sync")
        );

        asyncSource.Writer.ShouldBeAssignableTo<IAsyncSourceWriteBatchParticipant<AppSettings.Fragment>>();
        var result = await runtime.ApplyPatchesAsync([
            new StateSourcePatch(
                "sync",
                new AppSettings.Patch { RetryCount = FragmentOperation<int>.Set(4) }
            ),
            new StateSourcePatch(
                "async-base",
                new AppSettings.Patch { Label = FragmentOperation<string?>.Set("async") }
            ),
        ]);

        result.PhysicalWriteCount.ShouldBe(1);
        resource.WriteCount.ShouldBe(1);
    }

    private static ConfiglueRuntime<AppSettings, AppSettings.Fragment> BuildRuntime(
        IResourceReader firstResource,
        IResourceReader secondResource,
        string firstSection,
        string secondSection,
        string defaultSource
    )
    {
        var codec = new JsonStateCodec<AppSettings.Fragment>();
        return new ConfiglueRuntime<AppSettings, AppSettings.Fragment>(
            new StateSourceSet<AppSettings.Fragment>([
                SerializedStateSource.FromResource<AppSettings.Fragment>(
                    "first",
                    new JsonSectionResource(firstResource, firstSection),
                    codec,
                    priority: 10
                ),
                SerializedStateSource.FromResource<AppSettings.Fragment>(
                    "second",
                    new JsonSectionResource(secondResource, secondSection),
                    codec
                ),
            ]),
            StateWritePlan.DefaultTo(defaultSource)
        );
    }

    private static ConfiglueRuntime<AppSettings, AppSettings.Fragment> BuildRuntime(
        JsonSectionResource firstSection,
        JsonSectionResource secondSection,
        string defaultSource
    )
    {
        var codec = new JsonStateCodec<AppSettings.Fragment>();
        return new ConfiglueRuntime<AppSettings, AppSettings.Fragment>(
            new StateSourceSet<AppSettings.Fragment>([
                SerializedStateSource.FromResource<AppSettings.Fragment>(
                    "first",
                    firstSection,
                    codec,
                    priority: 10
                ),
                SerializedStateSource.FromResource<AppSettings.Fragment>(
                    "second",
                    secondSection,
                    codec
                ),
            ]),
            StateWritePlan.DefaultTo(defaultSource)
        );
    }

    private sealed class SharedBatchStore
    {
        private readonly object _gate = new();
        private byte[]? _content;
        private string? _revision;

        public SharedBatchStore(string resourceId) => ResourceId = new ResourceId(resourceId);

        public ResourceId ResourceId { get; }

        public long WriteCount { get; private set; }

        public ResourceReadResult Read()
        {
            lock (_gate)
            {
                return _content is null
                    ? ResourceReadResult.NotFound(_revision)
                    : ResourceReadResult.Success(_content, _revision);
            }
        }

        public string WriteBatch(IReadOnlyList<ResourceWriteMutation> mutations)
        {
            ResourceWriteMutation.ValidateBatch(mutations);
            lock (_gate)
            {
                var current = _content is null
                    ? ResourceReadResult.NotFound(_revision)
                    : ResourceReadResult.Success(_content, _revision);
                foreach (var mutation in mutations)
                {
                    var next = mutation.Apply(current).ToArray();
                    _content = next;
                    _revision = GetRevision(next);
                    current = ResourceReadResult.Success(next, _revision);
                }

                WriteCount++;
                return _revision!;
            }
        }

        private static string GetRevision(ReadOnlySpan<byte> content) =>
            Convert.ToHexString(SHA256.HashData(content));
    }

    private class SharedBatchResource
        : IResourceReader,
            IResourceBatchWriter,
            IResourceIdentity
    {
        public SharedBatchResource(SharedBatchStore store) => Store = store;

        protected SharedBatchStore Store { get; }

        public ResourceId ResourceId => Store.ResourceId;

        public ValueTask<ResourceReadResult> ReadAsync(
            ConfiglueResourceContext context,
            CancellationToken cancellationToken = default
        )
        {
            _ = context;
            cancellationToken.ThrowIfCancellationRequested();
            return new ValueTask<ResourceReadResult>(Store.Read());
        }

        public ValueTask<StateWriteResult> WriteAsync(
            ConfiglueResourceContext context,
            ResourceWriteRequest request,
            CancellationToken cancellationToken = default
        ) =>
            WriteBatchAsync(
                [ResourceWriteMutation.Replace(request, context)],
                cancellationToken
            );

        public ValueTask<StateWriteResult> WriteBatchAsync(
            IReadOnlyList<ResourceWriteMutation> mutations,
            CancellationToken cancellationToken = default
        )
        {
            cancellationToken.ThrowIfCancellationRequested();
            return new ValueTask<StateWriteResult>(
                new StateWriteResult(Store.WriteBatch(mutations))
            );
        }
    }

    private sealed class CompatibleSharedBatchResource(
        SharedBatchStore store,
        object token
    ) : SharedBatchResource(store), IResourceBatchCompatibility
    {
        public object? GetBatchCompatibilityToken(ConfiglueResourceContext context)
        {
            _ = context;
            return token;
        }
    }

    private sealed class PassthroughTransformer : IStateByteTransformer
    {
        public ReadOnlyMemory<byte> TransformRead(ReadOnlyMemory<byte> source) => source;

        public ReadOnlyMemory<byte> TransformWrite(ReadOnlyMemory<byte> source) => source;
    }
}
