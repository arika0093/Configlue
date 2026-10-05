using System.Diagnostics;
using Configlue.Resources;
using Configlue.Sources;
using Configlue.State;
using Configlue.Testing;

namespace Configlue.Tests;

public sealed class StateSourceResolverCacheTests
{
    [Test]
    public async Task IdleSubjectResolutions_AreEvictedOnAccessAndRebuilt()
    {
        var clock = new CacheClock();
        var source = CreateSource();
        var resolver = CreateResolver(source, clock, TimeSpan.FromMilliseconds(40));

        const int subjectCount = 64;
        for (var index = 0; index < subjectCount; index++)
        {
            var result = await resolver.ReadAsync(Context(source, index));
            result.Status.ShouldBe(StateReadStatus.Success);
        }

        resolver.SubjectResolutionCount.ShouldBe(subjectCount);

        clock.Advance(TimeSpan.FromMilliseconds(150));
        var reread = await resolver.ReadAsync(Context(source, 0));
        (reread.Status).ShouldBe(StateReadStatus.Success);
        (reread.Value!.RetryCount).ShouldBe(3);
        resolver.SubjectResolutionCount.ShouldBe(1);

        // A previously evicted subject must rebuild to the same value.
        var rebuilt = await resolver.ReadAsync(Context(source, 7));
        (rebuilt.Status).ShouldBe(StateReadStatus.Success);
        (rebuilt.Value!.RetryCount).ShouldBe(3);
        resolver.SubjectResolutionCount.ShouldBe(2);
    }

    [Test]
    public async Task ManySubjects_DoNotGrowTheCacheUnbounded()
    {
        var clock = new CacheClock();
        var source = CreateSource();
        var resolver = CreateResolver(source, clock, TimeSpan.FromMilliseconds(20));

        for (var index = 0; index < 60; index++)
        {
            await resolver.ReadAsync(Context(source, index));
            clock.Advance(TimeSpan.FromMilliseconds(5));
        }

        // Idle sweeps keep the cache proportional to the live window, not the number of subjects ever read.
        resolver.SubjectResolutionCount.ShouldBeLessThan(25);
    }

    [Test]
    public async Task ActiveWatch_SnapshotRemainsValidAfterIdleEviction()
    {
        var clock = new CacheClock();
        var source = CreateSource();
        var resolver = CreateResolver(source, clock, TimeSpan.FromMilliseconds(40));

        await resolver.ReadAsync(Context(source, 0));
        await resolver.ReadAsync(Context(source, 1));

        using (var watch = resolver.GetSourcesForWatch(Subject(0), RouteKey.Default, "revision"))
        {
            clock.Advance(TimeSpan.FromMilliseconds(150));
            await resolver.ReadAsync(Context(source, 2));
            // Watches capture an immutable snapshot and deliberately do not pin residency (issue
            // #271): both idle entries are evicted while the captured targets stay valid.
            resolver.SubjectResolutionCount.ShouldBe(1);
            watch.Targets.Count.ShouldBe(1);
            watch.Targets[0].ObservedRevision.ShouldBe("1");
        }

        clock.Advance(TimeSpan.FromMilliseconds(150));
        await resolver.ReadAsync(Context(source, 3));
        resolver.SubjectResolutionCount.ShouldBe(1);
    }

    [Test]
    public async Task EvictedSubject_StillReadsAndWatchesCorrectly()
    {
        var clock = new CacheClock();
        var source = CreateSource();
        var resolver = CreateResolver(source, clock, TimeSpan.FromMilliseconds(20));

        await resolver.ReadAsync(Context(source, 0));
        resolver.SubjectResolutionCount.ShouldBe(1);

        clock.Advance(TimeSpan.FromMilliseconds(60));
        await resolver.ReadAsync(Context(source, 1));
        resolver.SubjectResolutionCount.ShouldBe(1);

        // Watching an evicted subject falls back to the conservative full source list.
        using (var targets = resolver.GetSourcesForWatch(Subject(0), RouteKey.Default, "stale"))
        {
            targets.Targets.Count.ShouldBe(1);
            targets.Targets[0].ObservedRevision.ShouldBe("stale");
        }

        // A full watcher still completes for an evicted subject rather than throwing.
        var watcher = new StateSourceWatcher<AppSettings.Fragment>(resolver);
        await watcher.WaitForChangeAsync(Context(source, 0), "stale", CancellationToken.None);

        var rebuilt = await resolver.ReadAsync(Context(source, 0));
        (rebuilt.Status).ShouldBe(StateReadStatus.Success);
        (rebuilt.Value!.RetryCount).ShouldBe(3);
    }

    private static StateSourceResolver<AppSettings.Fragment> CreateResolver(
        StateSource<AppSettings.Fragment> source,
        CacheClock clock,
        TimeSpan idleTimeout
    ) =>
        new(
            new StateSourceSet<AppSettings.Fragment>([source]),
            subjectResolutionIdleTimeout: idleTimeout,
            getTimestamp: () => clock.Timestamp
        );

    private static StateSource<AppSettings.Fragment> CreateSource()
    {
        var store = new InMemoryStateSource<AppSettings.Fragment>(
            new AppSettings.Fragment { RetryCount = Optional<int>.Present(3) }
        );
        return new StateSource<AppSettings.Fragment>(
            "cache-source",
            store,
            new StateSourceOptions<AppSettings.Fragment> { Writer = store, Watcher = store }
        );
    }

    private static ConfiglueResourceContext Context(
        StateSource<AppSettings.Fragment> source,
        int index
    ) => source.GetResourceContext(Subject(index));

    private static CacheSubject Subject(int index) => new(index);

    private sealed class CacheClock
    {
        public long Timestamp { get; private set; }

        public void Advance(TimeSpan elapsed) =>
            Timestamp += (long)(elapsed.TotalSeconds * Stopwatch.Frequency);
    }

    private sealed record CacheSubject(int Index) : IConfiglueSubject
    {
        public SubjectKey Key => SubjectKey.From($"cache-subject-{Index}");
    }
}
