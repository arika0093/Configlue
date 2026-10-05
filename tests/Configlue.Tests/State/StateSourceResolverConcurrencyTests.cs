using System.Diagnostics;
using Configlue.Resources;
using Configlue.Sources;
using Configlue.State;
using Configlue.Testing;

namespace Configlue.Tests;

// Minimal concurrency coverage for the simplified subject cache (issue #271): updates publish
// via a single atomic dictionary assignment (last-writer-wins) and sweeps remove by exact
// key/value pair (a stale snapshot never evicts the fresh resident entry).
public sealed class StateSourceResolverConcurrencyTests
{
    [Test]
    public async Task ConcurrentSameSubjectReads_LastWriterWins()
    {
        var (store, source) = CreateSource(3);
        var resolver = CreateResolver(source, TimeSpan.Zero);
        var subject = Subject(0);
        var context = source.GetResourceContext(subject);

        _ = await resolver.ReadAsync(context);
        resolver.SubjectResolutionCount.ShouldBe(1);

        var workers = new Task[8];
        for (var worker = 0; worker < workers.Length; worker++)
        {
            workers[worker] = Task.Run(async () =>
            {
                for (var iteration = 0; iteration < 50; iteration++)
                {
                    var result = await resolver.ReadAsync(context).ConfigureAwait(false);
                    result.Status.ShouldBe(StateReadStatus.Success);
                }
            });
        }

        // A concurrent writer moves the value while readers hammer the same key; every
        // published entry is complete, so the resident entry is always one of the writes.
        var writer = Task.Run(() =>
        {
            for (var value = 0; value < 20; value++)
            {
                store.Set(Fragment(100 + value));
            }
        });

        await Task.WhenAll(workers.Concat([writer]));
        resolver.SubjectResolutionCount.ShouldBe(1);

        // Last-writer-wins: a final sequential write is visible to the next read and watch.
        store.Set(Fragment(7));
        var updated = await resolver.ReadAsync(context);
        updated.Status.ShouldBe(StateReadStatus.Success);
        (updated.Value!.RetryCount).ShouldBe(7);
        resolver.SubjectResolutionCount.ShouldBe(1);

        using var watch = resolver.GetSourcesForWatch(subject, RouteKey.Default, "fallback");
        watch.Targets.Count.ShouldBe(1);
        watch.Targets[0].ObservedRevision.ShouldNotBe("fallback");
    }

    [Test]
    public async Task EvictRacingWithUpdate_KeepsCacheConsistent()
    {
        // Deterministic slice: a refreshed entry survives the sweep that evicts its idle
        // sibling. Under concurrency the same guarantee holds via pair-remove: a sweep holding
        // a stale snapshot can only remove the exact entry it saw, never the fresh replacement.
        var (_, detSource) = CreateSource(3);
        var detClock = new CacheClock();
        var detResolver = new StateSourceResolver<AppSettings.Fragment>(
            new StateSourceSet<AppSettings.Fragment>([detSource]),
            subjectResolutionIdleTimeout: TimeSpan.FromMilliseconds(100),
            getTimestamp: () => detClock.Timestamp
        );
        var detFirst = Subject(0);
        var detSecond = Subject(1);
        _ = await detResolver.ReadAsync(detSource.GetResourceContext(detFirst));
        _ = await detResolver.ReadAsync(detSource.GetResourceContext(detSecond));
        detResolver.SubjectResolutionCount.ShouldBe(2);

        // The re-read refreshes the first entry and its own sweep evicts only the idle sibling.
        detClock.Advance(TimeSpan.FromMilliseconds(500));
        _ = await detResolver.ReadAsync(detSource.GetResourceContext(detFirst));
        detResolver.SubjectResolutionCount.ShouldBe(1);
        detResolver.EvictIdleSubjectResolutionsForTest().ShouldBe(0);
        detResolver.SubjectResolutionCount.ShouldBe(1);

        using (var freshWatch = detResolver.GetSourcesForWatch(detFirst, RouteKey.Default, "fallback"))
        {
            freshWatch.Targets.Count.ShouldBe(1);
            freshWatch.Targets[0].ObservedRevision.ShouldBe("1");
        }

        using (var staleWatch = detResolver.GetSourcesForWatch(detSecond, RouteKey.Default, "fallback"))
        {
            staleWatch.Targets.Count.ShouldBe(1);
            staleWatch.Targets[0].ObservedRevision.ShouldBe("fallback");
        }

        // Concurrent slice: updates race full sweeps. Nothing is idle (default 5-minute
        // timeout), so sweeps enumerate while replacements land; the count must not drift and
        // every resident entry stays complete.
        var (store, source) = CreateSource(3);
        var resolver = CreateResolver(source, TimeSpan.FromMinutes(5));
        var first = Subject(0);
        var second = Subject(1);
        var firstContext = source.GetResourceContext(first);
        var secondContext = source.GetResourceContext(second);

        _ = await resolver.ReadAsync(firstContext);
        _ = await resolver.ReadAsync(secondContext);
        resolver.SubjectResolutionCount.ShouldBe(2);

        using var stop = new CancellationTokenSource();
        var readers = new Task[4];
        for (var reader = 0; reader < readers.Length; reader++)
        {
            var captured = reader;
            readers[reader] = Task.Run(async () =>
            {
                var context = captured % 2 == 0 ? firstContext : secondContext;
                while (!stop.IsCancellationRequested)
                {
                    var result = await resolver.ReadAsync(context).ConfigureAwait(false);
                    result.Status.ShouldBe(StateReadStatus.Success);
                }
            });
        }

        var evictor = Task.Run(() =>
        {
            for (var iteration = 0; iteration < 200; iteration++)
            {
                _ = resolver.EvictIdleSubjectResolutionsForTest();
            }
        });

        store.Set(Fragment(7));
        await evictor;
        await stop.CancelAsync();
        await Task.WhenAll(readers);

        resolver.SubjectResolutionCount.ShouldBe(2);

        var reread = await resolver.ReadAsync(firstContext);
        reread.Status.ShouldBe(StateReadStatus.Success);
        (reread.Value!.RetryCount).ShouldBe(7);
        resolver.SubjectResolutionCount.ShouldBe(2);

        using var watch = resolver.GetSourcesForWatch(first, RouteKey.Default, "fallback");
        watch.Targets.Count.ShouldBe(1);
        watch.Targets[0].ObservedRevision.ShouldNotBe("fallback");
    }

    private static StateSourceResolver<AppSettings.Fragment> CreateResolver(
        StateSource<AppSettings.Fragment> source,
        TimeSpan idleTimeout
    ) =>
        new(
            new StateSourceSet<AppSettings.Fragment>([source]),
            subjectResolutionIdleTimeout: idleTimeout
        );

    private static (
        InMemoryStateSource<AppSettings.Fragment> Store,
        StateSource<AppSettings.Fragment> Source
    ) CreateSource(int retryCount)
    {
        var store = new InMemoryStateSource<AppSettings.Fragment>(Fragment(retryCount));
        return (
            store,
            new StateSource<AppSettings.Fragment>(
                "cache-source",
                store,
                new StateSourceOptions<AppSettings.Fragment> { Writer = store, Watcher = store }
            )
        );
    }

    private static AppSettings.Fragment Fragment(int retryCount) =>
        new() { RetryCount = Optional<int>.Present(retryCount) };

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
