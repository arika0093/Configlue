using Configlue.Provider.Json;
using Configlue.Resources;
using Configlue.Sources;
using Configlue.Testing;

namespace Configlue.Tests;

public sealed class BatchWriteParticipantContractTests
{
    [Test]
    public async Task ProjectedWriter_PreparesSyncAndAsyncBatchesForBatchCapableSources()
    {
        var resource = new InMemoryResource();
        var source = SerializedStateSource.FromResource<DatabaseSettings.Fragment>(
            "database",
            resource,
            new JsonStateCodec<DatabaseSettings.Fragment>()
        );
        var projected = Project(source);
        var value = new AppSettings.Fragment
        {
            Database = Optional<DatabaseSettings.Fragment?>.Present(
                new DatabaseSettings.Fragment { Host = Optional<string>.Present("batch.db") }
            ),
        };
        var request = new StateWriteRequest<AppSettings.Fragment>(value);
        var syncParticipant = (ISourceWriteBatchParticipant<AppSettings.Fragment>)projected.Writer!;

        syncParticipant
            .TryCreateBatchWrite(
                ConfiglueResourceContext.Default,
                request,
                out var resourceId,
                out var batchWriter,
                out var mutation
            )
            .ShouldBeTrue();
        resourceId.ShouldBe(resource.ResourceId);
        batchWriter.ShouldBeSameAs(resource);
        mutation.ShouldNotBeNull();
        await batchWriter!.WriteBatchAsync([mutation!]);

        var asyncParticipant =
            (IAsyncSourceWriteBatchParticipant<AppSettings.Fragment>)projected.Writer!;
        asyncParticipant.CanPrepareBatchWrite.ShouldBeTrue();
        var plan = await asyncParticipant.TryCreateBatchWriteAsync(
            ConfiglueResourceContext.Default,
            request
        );

        plan.ShouldNotBeNull();
        plan.Value.ResourceId.ShouldBe(resource.ResourceId);
        await plan.Value.BatchWriter.WriteBatchAsync([plan.Value.Mutation]);
        resource.WriteCount.ShouldBe(2);
        (await projected.Reader.ReadAsync()).Value!.Database!.Value!.Host.Value.ShouldBe(
            "batch.db"
        );
    }

    [Test]
    public async Task ProjectedWriter_ReportsUnsupportedBatchPreparation()
    {
        var store = new InMemoryStateSource<DatabaseSettings.Fragment>();
        var source = new StateSource<DatabaseSettings.Fragment>("database", store, writer: store);
        var projected = Project(source);
        var request = new StateWriteRequest<AppSettings.Fragment>(new AppSettings.Fragment());
        var syncParticipant = (ISourceWriteBatchParticipant<AppSettings.Fragment>)projected.Writer!;
        var asyncParticipant =
            (IAsyncSourceWriteBatchParticipant<AppSettings.Fragment>)projected.Writer!;

        syncParticipant
            .TryCreateBatchWrite(
                ConfiglueResourceContext.Default,
                request,
                out var resourceId,
                out var batchWriter,
                out var mutation
            )
            .ShouldBeFalse();
        resourceId.ShouldBe(default(ResourceId));
        batchWriter.ShouldBeNull();
        mutation.ShouldBeNull();
        asyncParticipant.CanPrepareBatchWrite.ShouldBeFalse();
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
}
