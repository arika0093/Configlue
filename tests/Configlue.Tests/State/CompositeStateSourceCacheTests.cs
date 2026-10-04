using System.Collections.Concurrent;
using Configlue.Sources;

namespace Configlue.Tests;

public sealed class CompositeStateSourceCacheTests
{
    [Test]
    public async Task WatchTargetsRemainBoundedAndActiveLeasesSurviveEvictionRaces()
    {
        var component = new TrackingComponent();
        var source = new StateSource<AppSettings.Fragment>("component", component, new StateSourceOptions<AppSettings.Fragment> { Watcher = component });
        var composite = new CompositeStateSource<AppSettings.Fragment>(
            new StateSourceSet<AppSettings.Fragment>([source]),
            defaultWriteSourceId: null,
            writePlan: null,
            watchTargetIdleTimeout: TimeSpan.Zero,
            watchTargetCapacity: 8
        );

        for (var index = 0; index < 100; index++)
        {
            await composite.ReadAsync(Context(new CacheSubject($"one-shot-{index}")));
            composite.WatchTargetCount.ShouldBeLessThanOrEqualTo(1);
        }

        var subject = new CacheSubject("watched");
        var context = Context(subject);
        var firstRead = await composite.ReadAsync(context);

        var leaseAcquired = NewSignal();
        var allowLeaseReturn = NewSignal();
        var interceptNextLease = 1;
        composite.BeforeWatchLeaseReturn = () =>
        {
            if (Interlocked.Exchange(ref interceptNextLease, 0) == 1)
            {
                leaseAcquired.TrySetResult();
                allowLeaseReturn.Task.GetAwaiter().GetResult();
            }
        };
        var waiting = Task.Run(async () => await composite.WaitForChangeAsync(context, firstRead.Revision));
        await leaseAcquired.Task.WaitAsync(TimeSpan.FromSeconds(5));

        composite.EvictIdleWatchTargetsForTest();
        composite.WatchTargetCount.ShouldBe(1);
        allowLeaseReturn.TrySetResult();
        await component.WatchStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        component.ObservedRevisions.Last().ShouldBe("revision:101");

        composite.EvictIdleWatchTargetsForTest();
        composite.WatchTargetCount.ShouldBe(1);
        component.ReleaseWatch();
        await waiting.WaitAsync(TimeSpan.FromSeconds(5));

        composite.EvictIdleWatchTargetsForTest();
        composite.WatchTargetCount.ShouldBe(0);

        for (var index = 0; index < 5; index++)
        {
            var reread = await composite.ReadAsync(context);
            await composite.WaitForChangeAsync(context, reread.Revision);
            component.ObservedRevisions.Last().ShouldBe($"revision:{102 + index}");
            composite.WatchTargetCount.ShouldBe(1);

            composite.EvictIdleWatchTargetsForTest();
            composite.WatchTargetCount.ShouldBe(0);
        }
    }

    private static ConfiglueResourceContext Context(IConfiglueSubject subject) =>
        new(subject, ResourceKey.From("outer"), RouteKey.From("outer"));

    private static TaskCompletionSource NewSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private sealed record CacheSubject(string Name) : IConfiglueSubject
    {
        public SubjectKey Key => SubjectKey.From(Name);
    }

    private sealed class TrackingComponent
        : ISourceReader<AppSettings.Fragment>,
            ISourceWatcher
    {
        private int _readCount;
        private readonly TaskCompletionSource _watchRelease = NewSignal();

        public TaskCompletionSource WatchStarted { get; } = NewSignal();

        public ConcurrentQueue<string?> ObservedRevisions { get; } = new();

        public void ReleaseWatch() => _watchRelease.TrySetResult();

        public ValueTask<StateReadResult<AppSettings.Fragment>> ReadAsync(
            ConfiglueResourceContext context,
            CancellationToken cancellationToken = default
        )
        {
            cancellationToken.ThrowIfCancellationRequested();
            var revision = Interlocked.Increment(ref _readCount);
            return ValueTaskCompat.FromResult(
                StateReadResult<AppSettings.Fragment>.Success(
                    new AppSettings.Fragment(),
                    $"revision:{revision}"
                )
            );
        }

        public async ValueTask WaitForChangeAsync(
            ConfiglueResourceContext context,
            string? observedRevision,
            CancellationToken cancellationToken = default
        )
        {
            ObservedRevisions.Enqueue(observedRevision);
            WatchStarted.TrySetResult();
            await _watchRelease.Task.WaitAsync(cancellationToken);
        }
    }
}
