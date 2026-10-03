using System.Buffers;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using Configlue.Extensibility;
using Configlue.Provider.Json;

namespace Configlue.Tests;

public sealed class ContextualResourceContractTests
{
    [Test]
    public async Task FromResourcePreservesContextualIdentityAndBatchWrites()
    {
        var resource = new ContextualMemoryResource(perSubjectIdentity: true);
        var codec = new JsonStateCodec<AppSettings.Fragment>();
        var source = SerializedStateSource.FromResource<AppSettings.Fragment>("contextual", resource, codec);
        var first = new SettingsSubject("a", "one");
        var second = new SettingsSubject("b", "two");
        source.GetResourceId(first).ShouldBe(resource.GetResourceId(Context(first)));
        source.GetResourceId(second).ShouldBe(resource.GetResourceId(Context(second)));
        source.GetResourceId(first).ShouldNotBe(source.GetResourceId(second));
        var projected = StateSourceProjection.Project(source, static fragment => fragment, static fragment => fragment);
        projected.GetResourceId(first).ShouldBe(source.GetResourceId(first));
        projected.GetResourceId(second).ShouldBe(source.GetResourceId(second));
        var readOnlyProjection = StateSourceProjection.Project(source, static fragment => fragment);
        readOnlyProjection.GetResourceId(first).ShouldBe(source.GetResourceId(first));
        var participant = (IAsyncSourceWriteBatchParticipant<AppSettings.Fragment>)source.Writer!;
        var plan = await participant.TryCreateBatchWriteAsync(
            Context(first),
            new StateWriteRequest<AppSettings.Fragment>(Fragment("batch"))
        );
        plan.ShouldNotBeNull();
        ((ResourceId?)plan.Value.ResourceId).ShouldBe(source.GetResourceId(first));
        await plan.Value.BatchWriter.WriteBatchAsync([plan.Value.Mutation]);
        (await source.ReadAsync(first)).Value!.Label.Value.ShouldBe("batch");
        var fixedId = new ResourceId("fixed:override");
        var fixedSource = SerializedStateSource.FromResource<AppSettings.Fragment>("fixed", resource, codec, fixedResourceId: fixedId);
        fixedSource.GetResourceId(first).ShouldBe(fixedId);
        fixedSource.GetResourceId(second).ShouldBe(fixedId);
        StateSourceProjection.Project(fixedSource, static fragment => fragment).GetResourceId(first).ShouldBe(fixedId);
    }

    [Test]
    public async Task SerializedAdaptersPassContextThroughPipelineWritesAndBatchMutations()
    {
        var codec = new JsonStateCodec<AppSettings.Fragment>();
        var resource = new ContextualMemoryResource(perSubjectIdentity: true);
        var subjectA = new SettingsSubject("tenant-a", "user-a");
        var subjectB = new SettingsSubject("tenant-b", "user-b");
        resource.Set(subjectA.Key, Serialize(codec, Fragment("before-a")));
        resource.Set(subjectB.Key, Serialize(codec, Fragment("before-b")));

        var reader = new SerializedStateReader<AppSettings.Fragment>(resource, codec);
        var writer = new SerializedStateWriter<AppSettings.Fragment>(resource, codec);
        var source = new StateSource<AppSettings.Fragment>(
            "subject-settings",
            reader,
            writer: writer,
            modelId: "subject-settings-model"
        );

        source.GetResourceContext(subjectA).ModelId.ShouldBe("subject-settings-model");
        var initial = await source.ReadAsync(subjectA);
        initial.Status.ShouldBe(StateReadStatus.Success);
        initial.Value!.Label.Value.ShouldBe("before-a");
        resource.LastPipelineContext!.Value.ResourceKey.ShouldBe(ResourceKey.From(subjectA.Key));
        resource.LastPipelineContext.Value.Subject.ShouldBe(subjectA);
        resource.LastPipelineContext.Value.ModelId.ShouldBe("subject-settings-model");
        source.GetResourceId(subjectA).ShouldBe(new ResourceId($"object:{subjectA.Key.Value}"));
        source.GetResourceId(subjectB).ShouldBe(new ResourceId($"object:{subjectB.Key.Value}"));
        source.GetResourceId(subjectA).ShouldNotBe(source.GetResourceId(subjectB));

        await source.WriteAsync(
            subjectA,
            new StateWriteRequest<AppSettings.Fragment>(
                Fragment("after-a"),
                RevisionCondition.FromRevision(initial.Revision)
            )
        );
        resource.LastWriteContext!.Value.ResourceKey.ShouldBe(ResourceKey.From(subjectA.Key));

        var afterWrite = await source.ReadAsync(subjectA);
        afterWrite.Value!.Label.Value.ShouldBe("after-a");

        var batchRequest = new StateWriteRequest<AppSettings.Fragment>(
            Fragment("batch-b"),
            RevisionCondition.FromRevision((await source.ReadAsync(subjectB)).Revision)
        );
        var context = new ConfiglueResourceContext(
            "subject-settings-model",
            subjectB,
            ResourceKey.From(subjectB.Key),
            RouteKey.Default
        );
        var batchPlan = await writer.TryCreateBatchWriteAsync(context, batchRequest);
        batchPlan.ShouldNotBeNull();
        batchPlan.Value.ResourceId.ShouldBe(new ResourceId($"object:{subjectB.Key.Value}"));
        batchPlan.Value.Mutation.Context.ResourceKey.ShouldBe(ResourceKey.From(subjectB.Key));
        batchPlan.Value.Mutation.Context.Subject.ShouldBe(subjectB);
        batchPlan.Value.Mutation.Context.ModelId.ShouldBe("subject-settings-model");
        (await batchPlan.Value.BatchWriter.WriteBatchAsync([batchPlan.Value.Mutation])).Revision.ShouldNotBeNull();
        resource.LastWriteContext!.Value.ResourceKey.ShouldBe(ResourceKey.From(subjectB.Key));
        (await source.ReadAsync(subjectB)).Value!.Label.Value.ShouldBe("batch-b");
    }

    [Test]
    public void ResourceIdentityCanShareOrSeparateCoordinationDomainsByContext()
    {
        var first = new SettingsSubject("tenant", "one");
        var second = new SettingsSubject("tenant", "two");
        var sharedIdentity = new ContextualMemoryResource(perSubjectIdentity: false);
        var separateIdentity = new ContextualMemoryResource(perSubjectIdentity: true);

        sharedIdentity.GetResourceId(Context(first)).ShouldBe(sharedIdentity.ResourceId);
        sharedIdentity.GetResourceId(Context(second)).ShouldBe(sharedIdentity.ResourceId);
        separateIdentity
            .GetResourceId(Context(first))
            .ShouldNotBe(separateIdentity.GetResourceId(Context(second)));
    }

    private static ConfiglueResourceContext Context(SettingsSubject subject) =>
        new(subject, ResourceKey.From(subject.Key), RouteKey.Default);

    private static AppSettings.Fragment Fragment(string? label) =>
        new() { Label = Optional<string?>.Present(label) };

    private static ReadOnlyMemory<byte> Serialize(
        JsonStateCodec<AppSettings.Fragment> codec,
        AppSettings.Fragment fragment
    )
    {
        var buffer = new ArrayBufferWriter<byte>();
        var context = default(StateCodecContext);
        codec.Serialize(fragment, buffer, in context);
        return buffer.WrittenMemory.ToArray();
    }

    private sealed record SettingsSubject(string TenantId, string UserId) : IConfiglueSubject
    {
        public SubjectKey Key => SubjectKey.FromSegments(TenantId, UserId);
    }

    private sealed class ContextualMemoryResource(bool perSubjectIdentity)
        : IResourceReader,
            IPipelineResourceReader,
            IResourceBatchWriter,
            IResourceWriter,
            IResourceIdentity
    {
        private readonly ConcurrentDictionary<ResourceKey, ResourceReadResult> _states = new();

        public ResourceId ResourceId { get; } = new("memory:shared");

        public bool IsPipelineReadPreferred => true;

        public ConfiglueResourceContext? LastPipelineContext { get; private set; }

        public ConfiglueResourceContext? LastWriteContext { get; private set; }

        public ResourceId GetResourceId(ConfiglueResourceContext context) =>
            perSubjectIdentity ? new ResourceId($"object:{context.ResourceKey.Value}") : ResourceId;

        public void Set(SubjectKey key, ReadOnlyMemory<byte> content) =>
            _states[ResourceKey.From(key)] = ResourceReadResult.Success(content, Revision(content.Span));

        public ValueTask<ResourceReadResult> ReadAsync(
            CancellationToken cancellationToken = default
        ) => ReadAsync(ConfiglueResourceContext.Default, cancellationToken);

        public ValueTask<ResourceReadResult> ReadAsync(
            ConfiglueResourceContext context,
            CancellationToken cancellationToken = default
        )
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTaskCompat.FromResult(
                _states.TryGetValue(context.ResourceKey, out var state)
                    ? state
                    : ResourceReadResult.NotFound()
            );
        }

        public async ValueTask<PipelineResourceReadResult> ReadPipelineAsync(
            CancellationToken cancellationToken = default
        ) =>
            await ReadPipelineAsync(ConfiglueResourceContext.Default, cancellationToken)
                .ConfigureAwait(false);

        public async ValueTask<PipelineResourceReadResult> ReadPipelineAsync(
            ConfiglueResourceContext context,
            CancellationToken cancellationToken = default
        )
        {
            LastPipelineContext = context;
            var result = await ReadAsync(context, cancellationToken).ConfigureAwait(false);
            return await PipelineResourceReader
                .FromMemoryAsync(result, cancellationToken)
                .ConfigureAwait(false);
        }

        public ValueTask<StateWriteResult> WriteAsync(
            ResourceWriteRequest request,
            CancellationToken cancellationToken = default
        ) => WriteAsync(ConfiglueResourceContext.Default, request, cancellationToken);

        public ValueTask<StateWriteResult> WriteAsync(
            ConfiglueResourceContext context,
            ResourceWriteRequest request,
            CancellationToken cancellationToken = default
        ) => WriteBatchAsync([ResourceWriteMutation.Replace(request, context)], cancellationToken);

        public ValueTask<StateWriteResult> WriteBatchAsync(
            IReadOnlyList<ResourceWriteMutation> mutations,
            CancellationToken cancellationToken = default
        )
        {
            cancellationToken.ThrowIfCancellationRequested();
            ResourceWriteMutation.ValidateBatch(mutations);
            StateWriteResult result = default;
            foreach (var mutation in mutations)
            {
                cancellationToken.ThrowIfCancellationRequested();
                LastWriteContext = mutation.Context;
                var current = _states.TryGetValue(mutation.Context.ResourceKey, out var value)
                    ? value
                    : ResourceReadResult.NotFound();
                if (
                    !mutation.Condition.IsSatisfiedBy(
                        current.Revision,
                        current.Status == StateReadStatus.Success
                    )
                )
                {
                    throw new StateConflictException("The resource changed after it was read.");
                }

                var content = mutation.Apply(current).ToArray();
                var revision = Revision(content);
                _states[mutation.Context.ResourceKey] = ResourceReadResult.Success(
                    content,
                    revision,
                    mutation.Schema
                );
                result = new StateWriteResult(revision);
            }

            return ValueTaskCompat.FromResult(result);
        }

        private static string Revision(ReadOnlySpan<byte> content) =>
            Convert.ToHexString(SHA256.HashData(content));
    }
}
