using Configlue.Provider.Json;
using Configlue.Resources;
using Configlue.Sources;
using Configlue.Testing;

namespace Configlue.Tests;

public sealed class BatchWriteParticipantContractTests
{
    [Test]
    public async Task ProjectedWriter_PreparesBatchForBatchCapableSources()
    {
        var resource = new InMemoryResource();
        var source = new StateSource<DatabaseSettings.Fragment>("database", new SerializedSource<DatabaseSettings.Fragment>(resource, new JsonStateCodec<DatabaseSettings.Fragment>(), writer: (IResourceReader)resource as IResourceWriter, watcher: (IResourceReader)resource as ISourceWatcher), new StateSourceOptions<DatabaseSettings.Fragment>());
        var projected = Project(source);
        var value = new AppSettings.Fragment
        {
            Database = Optional<DatabaseSettings.Fragment?>.Present(
                new DatabaseSettings.Fragment { Host = Optional<string>.Present("batch.db") }
            ),
        };
        var request = new StateWriteRequest<AppSettings.Fragment>(value);
        var asyncParticipant =
            (IAsyncSourceWriteBatchParticipant<AppSettings.Fragment>)projected.Writer!;
        asyncParticipant
            .TryCreateBatchWriteAsync(ConfiglueResourceContext.Default, request)
            .IsCompletedSuccessfully.ShouldBeTrue();
        var plan = await asyncParticipant.TryCreateBatchWriteAsync(
            ConfiglueResourceContext.Default,
            request
        );

        plan.ShouldNotBeNull();
        plan!.ResourceId.ShouldBe(resource.ResourceId);
        using var ownedPlan = plan;
        await plan.BatchWriter.WriteBatchAsync([plan.Mutation]);
        resource.WriteCount.ShouldBe(1);
        (await projected.Reader.ReadAsync()).Value!.Database!.Value!.Host.Value.ShouldBe(
            "batch.db"
        );
    }

    [Test]
    public void StateWriteBatchPlan_RejectsInvalidRequiredMembers()
    {
        var writer = new InMemoryResource();
        var mutation = ResourceWriteMutation.Replace(
            new ResourceWriteRequest(ReadOnlyMemory<byte>.Empty),
            ConfiglueResourceContext.Default
        );
        var id = writer.ResourceId;

        Should.Throw<ArgumentException>(() => new StateWriteBatchPlan(default, writer, mutation));
        Should.Throw<ArgumentNullException>(() => new StateWriteBatchPlan(id, null!, mutation));
        Should.Throw<ArgumentNullException>(() => new StateWriteBatchPlan(id, writer, null!));
    }

    [Test]
    public void StateWriteBatchPlan_ReleasesContentOwnerExactlyOnce()
    {
        var writer = new InMemoryResource();
        var owner = new CountingOwner();
        var bytes = new byte[] { 1, 2, 3 };
        var request = new ResourceWriteRequest(bytes)
        {
            ContentIsOwned = true,
            ContentOwner = owner,
        };
        var mutation = ResourceWriteMutation.Replace(request, ConfiglueResourceContext.Default);
        var returned = mutation.Apply(ResourceReadResult.NotFound());

        System.Runtime.InteropServices.MemoryMarshal.TryGetArray(returned, out var segment)
            .ShouldBeTrue();
        ReferenceEquals(segment.Array, bytes).ShouldBeTrue();

        var plan = new StateWriteBatchPlan(writer.ResourceId, writer, mutation);
        plan.SetContentOwner(owner);
        plan.Dispose();
        plan.Dispose();

        owner.DisposeCount.ShouldBe(1);
    }

    [Test]
    public async Task AsyncParticipantCanYieldAndHonorsCancellation()
    {
        var participant = new DelayedParticipant();
        var pending = participant.TryCreateBatchWriteAsync(
            ConfiglueResourceContext.Default,
            new StateWriteRequest<AppSettings.Fragment>(new AppSettings.Fragment())
        );

        pending.IsCompleted.ShouldBeFalse();
        participant.Complete();
        (await pending).ShouldBeNull();

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Should.ThrowAsync<OperationCanceledException>(async () =>
            await participant.TryCreateBatchWriteAsync(
                ConfiglueResourceContext.Default,
                new StateWriteRequest<AppSettings.Fragment>(new AppSettings.Fragment()),
                cancellation.Token
            )
        );
    }

    [Test]
    public async Task ProjectedWriter_ReportsUnsupportedBatchPreparation()
    {
        var store = new InMemoryStateSource<DatabaseSettings.Fragment>();
        var source = new StateSource<DatabaseSettings.Fragment>("database", store, new StateSourceOptions<DatabaseSettings.Fragment> { Writer = store });
        var projected = Project(source);
        var request = new StateWriteRequest<AppSettings.Fragment>(new AppSettings.Fragment());
        var asyncParticipant =
            (IAsyncSourceWriteBatchParticipant<AppSettings.Fragment>)projected.Writer!;
        (
            await asyncParticipant.TryCreateBatchWriteAsync(
                ConfiglueResourceContext.Default,
                request
            )
        ).ShouldBeNull();
    }

    private static StateSource<AppSettings.Fragment> Project(
        StateSource<DatabaseSettings.Fragment> source
    ) =>
        StateSourceProjection.Project<DatabaseSettings.Fragment, AppSettings.Fragment>(
            source,
            fragment => new AppSettings.Fragment
            {
                Database = Optional<DatabaseSettings.Fragment?>.Present(fragment),
            },
            root =>
                root.Database.IsPresent ? root.Database.Value! : DatabaseSettings.Fragment.Empty,
            AppSettings.ConfiglueSchema.ToMetadata()
        );

    private sealed class DelayedParticipant : IAsyncSourceWriteBatchParticipant<AppSettings.Fragment>
    {
        private readonly TaskCompletionSource _completion = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );

        public async ValueTask<StateWriteBatchPlan?> TryCreateBatchWriteAsync(
            ConfiglueResourceContext context,
            StateWriteRequest<AppSettings.Fragment> request,
            CancellationToken cancellationToken = default
        )
        {
            cancellationToken.ThrowIfCancellationRequested();
            await _completion.Task.WaitAsync(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            return null;
        }

        public void Complete() => _completion.TrySetResult();
    }

    private sealed class CountingOwner : IDisposable
    {
        public int DisposeCount { get; private set; }

        public void Dispose() => DisposeCount++;
    }
}
