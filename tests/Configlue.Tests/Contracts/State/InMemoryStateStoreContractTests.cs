using Configlue.Testing;

namespace Configlue.Tests;

public sealed class InMemoryStateStoreContractTests
{
    [Test]
    public async Task IStateReader_ReportsNotFoundUnavailableAndCurrentValue()
    {
        var store = new InMemoryStateStore<string>();
        IStateReader<string> reader = store;

        var missing = await reader.ReadAsync();
        store.SetUnavailable();
        var unavailable = await reader.ReadAsync();
        store.Set("ready");
        var current = await reader.ReadAsync();

        missing.Status.ShouldBe(StateReadStatus.NotFound);
        unavailable.Status.ShouldBe(StateReadStatus.Unavailable);
        current.Status.ShouldBe(StateReadStatus.Success);
        current.Value.ShouldBe("ready");
        current.Revision.ShouldNotBeNull();
    }

    [Test]
    public async Task IStateWriter_RequiresTheExpectedRevisionAndReturnsTheNewRevision()
    {
        var store = new InMemoryStateStore<string>("initial");
        IStateWriter<string> writer = store;
        var initial = await store.ReadAsync();

        var written = await writer.WriteAsync(
            new StateWriteRequest<string>(
                "updated",
                Condition: RevisionCondition.FromRevision(initial.Revision)
            )
        );

        written.Revision.ShouldNotBe(initial.Revision);
        (await store.ReadAsync()).Value.ShouldBe("updated");
        await Should.ThrowAsync<StateConflictException>(async () =>
            await writer.WriteAsync(
                new StateWriteRequest<string>(
                    "stale",
                    Condition: RevisionCondition.FromRevision(initial.Revision)
                )
            )
        );
        (await store.ReadAsync()).Value.ShouldBe("updated");
    }

    [Test]
    public async Task IStateWatcher_ObservesChangesAndHonorsCancellation()
    {
        var store = new InMemoryStateStore<string>("initial");
        IStateWatcher watcher = store;
        var initial = await store.ReadAsync();
        var change = watcher.WaitForChangeAsync(initial.Revision).AsTask();

        store.Set("updated");
        await change.WaitAsync(TimeSpan.FromSeconds(1));
        await watcher.WaitForChangeAsync(initial.Revision);

        using var cancellation = new CancellationTokenSource();
        var current = await store.ReadAsync();
        var pendingChange = watcher
            .WaitForChangeAsync(current.Revision, cancellation.Token)
            .AsTask();
        cancellation.Cancel();

        await Should.ThrowAsync<OperationCanceledException>(async () => await pendingChange);
    }
}
