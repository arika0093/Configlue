using Configlue.Testing;

namespace Configlue.Tests;

public sealed class StateRuntimeTests
{
    [Test]
    public async Task Resolver_FallsBackByPolicyAndWatchesHigherPrioritySourceForFailback()
    {
        var primary = new InMemoryStateStore<string>();
        primary.SetUnavailable();
        var fallback = new InMemoryStateStore<string>("local");
        var sources = new StateSourceSet<string>(
        [
            new StateSource<string>("remote", primary, priority: 100,
                fallbackCondition: StateFallbackCondition.NotFoundOrUnavailable, writer: primary, watcher: primary),
            new StateSource<string>("local", fallback, priority: 0, writer: fallback, watcher: fallback),
        ]);
        var runtime = new CompositeStateRuntime<string>(sources, StateWriteRoute.To("local"));

        var resolved = await runtime.Reader.ReadAsync();
        await runtime.Writer.WriteAsync(new StateWriteRequest<string>("edited locally", resolved.Revision));
        var localAfterWrite = await fallback.ReadAsync();
        var failbackWait = runtime.Watcher.WaitForChangeAsync(localAfterWrite.Revision).AsTask();
        primary.Set("remote");
        await failbackWait;
        var recovered = await runtime.Reader.ReadAsync();

        await Assert.That(resolved.Value).IsEqualTo("local");
        await Assert.That(resolved.SourceId).IsEqualTo("local");
        await Assert.That(resolved.Revisions!.Revisions.Count).IsEqualTo(2);
        await Assert.That(localAfterWrite.Value).IsEqualTo("edited locally");
        await Assert.That(recovered.Value).IsEqualTo("remote");
        await Assert.That(recovered.SourceId).IsEqualTo("remote");
        await Assert.That(runtime.Reader.ActiveSource!.Id).IsEqualTo("remote");
    }
}
