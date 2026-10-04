using Configlue.Resources;
using Configlue.Sources;
using Configlue.State;
using Configlue.Testing;

namespace Configlue.Tests;

public sealed class StateSourceResolverConcurrencyTests
{
    [Test]
    public async Task UpdateRacingWithEviction_LandsOnTheResidentEntry()
    {
        var (store, source) = CreateSource(3);
        var resolver = CreateResolver(source, TimeSpan.Zero);
        var subject = Subject(0);
        var context = source.GetResourceContext(subject);

        _ = await resolver.ReadAsync(context);
        resolver.SubjectResolutionCount.ShouldBe(1);

        store.Set(Fragment(7));

        using var entered = new SemaphoreSlim(0);
        using var release = new SemaphoreSlim(0);
        var firstLookup = 0;
        resolver.SubjectResolutionTestHooks = new SubjectResolutionCacheTestHooks
        {
            AfterUpdateLookup = () =>
            {
                if (Interlocked.Exchange(ref firstLookup, 1) != 0)
                {
                    return;
                }

                entered.Release();
                release.Wait();
            },
        };

        var updater = RunOnDedicatedThread(() =>
            resolver.ReadAsync(context).AsTask().GetAwaiter().GetResult()
        );

        try
        {
            (await entered.WaitAsync(TimeSpan.FromSeconds(10))).ShouldBeTrue();
            // The updater has looked up the resident entry; evict it before it applies the update.
            resolver.EvictIdleSubjectResolutionsForTest().ShouldBe(1);
            resolver.SubjectResolutionCount.ShouldBe(0);
        }
        finally
        {
            release.Release();
        }

        var updated = await updater;
        updated.Status.ShouldBe(StateReadStatus.Success);
        resolver.SubjectResolutionCount.ShouldBe(1);

        using var watch = resolver.GetSourcesForWatch(subject, RouteKey.Default, "fallback");
        watch.Targets.Count.ShouldBe(1);
        // The update landed on the entry that is currently resident for the key, not a detached one.
        watch.Targets[0].ObservedRevision.ShouldBe("2");
    }

    [Test]
    public async Task LeaseLookupRacingWithEviction_FallsBackInsteadOfLeasingAnEvictedEntry()
    {
        var (_, source) = CreateSource(3);
        var resolver = CreateResolver(source, TimeSpan.Zero);
        var subject = Subject(0);
        var context = source.GetResourceContext(subject);

        _ = await resolver.ReadAsync(context);
        resolver.SubjectResolutionCount.ShouldBe(1);

        using var entered = new SemaphoreSlim(0);
        using var release = new SemaphoreSlim(0);
        var firstLookup = 0;
        resolver.SubjectResolutionTestHooks = new SubjectResolutionCacheTestHooks
        {
            AfterWatchLookup = () =>
            {
                if (Interlocked.Exchange(ref firstLookup, 1) != 0)
                {
                    return;
                }

                entered.Release();
                release.Wait();
            },
        };

        var watcher = RunOnDedicatedThread(() =>
            resolver.GetSourcesForWatch(subject, RouteKey.Default, "fallback")
        );

        try
        {
            (await entered.WaitAsync(TimeSpan.FromSeconds(10))).ShouldBeTrue();
            resolver.EvictIdleSubjectResolutionsForTest().ShouldBe(1);
            resolver.SubjectResolutionCount.ShouldBe(0);
        }
        finally
        {
            release.Release();
        }

        using var captured = await watcher;
        captured.Targets.Count.ShouldBe(1);
        // The lookup lost to eviction, so the lease must not pin the detached entry: fall back conservatively.
        captured.Targets[0].ObservedRevision.ShouldBe("fallback");
        resolver.SubjectResolutionCount.ShouldBe(0);
    }

    [Test]
    public async Task ReleaseRacingWithEviction_KeepsTheEntryResidentUntilReleaseCompletes()
    {
        var (_, source) = CreateSource(3);
        var resolver = CreateResolver(source, TimeSpan.Zero);
        var subject = Subject(0);
        var context = source.GetResourceContext(subject);

        _ = await resolver.ReadAsync(context);
        var watch = resolver.GetSourcesForWatch(subject, RouteKey.Default, "1");
        resolver.SubjectResolutionCount.ShouldBe(1);

        using var entered = new SemaphoreSlim(0);
        using var release = new SemaphoreSlim(0);
        resolver.SubjectResolutionTestHooks = new SubjectResolutionCacheTestHooks
        {
            BeforeWatchLeaseRelease = () =>
            {
                entered.Release();
                release.Wait();
            },
        };

        var releaser = RunOnDedicatedThread(watch.Dispose);

        try
        {
            (await entered.WaitAsync(TimeSpan.FromSeconds(10))).ShouldBeTrue();
            // The release is in flight but the lease is still counted, so eviction must not remove the entry.
            resolver.EvictIdleSubjectResolutionsForTest().ShouldBe(0);
            resolver.SubjectResolutionCount.ShouldBe(1);
        }
        finally
        {
            release.Release();
        }

        await releaser;
        // Once the lease is released the entry becomes evictable again.
        resolver.EvictIdleSubjectResolutionsForTest().ShouldBe(1);
        resolver.SubjectResolutionCount.ShouldBe(0);
    }

    [Test]
    public async Task ReleaseAfterForcedEviction_DoesNotCorruptTheNewResidentEntry()
    {
        var (_, source) = CreateSource(3);
        var resolver = CreateResolver(source, TimeSpan.Zero);
        var subject = Subject(0);
        var context = source.GetResourceContext(subject);

        _ = await resolver.ReadAsync(context);
        var staleWatch = resolver.GetSourcesForWatch(subject, RouteKey.Default, "1");
        resolver.SubjectResolutionCount.ShouldBe(1);

        // Simulate a stale lease whose entry is evicted anyway; releasing it later must be harmless.
        resolver.ForceEvictSubjectResolutionForTest(subject, RouteKey.Default).ShouldBeTrue();
        resolver.SubjectResolutionCount.ShouldBe(0);

        _ = await resolver.ReadAsync(context);
        resolver.SubjectResolutionCount.ShouldBe(1);

        staleWatch.Dispose();
        staleWatch.Dispose();
        resolver.SubjectResolutionCount.ShouldBe(1);

        using var freshWatch = resolver.GetSourcesForWatch(subject, RouteKey.Default, "fallback");
        freshWatch.Targets.Count.ShouldBe(1);
        freshWatch.Targets[0].ObservedRevision.ShouldBe("1");
    }

    [Test]
    public async Task ConcurrentReplacement_RetriesAgainstTheNewResidentEntry()
    {
        var (store, source) = CreateSource(3);
        var resolver = CreateResolver(source, TimeSpan.Zero);
        var subject = Subject(0);
        var context = source.GetResourceContext(subject);

        _ = await resolver.ReadAsync(context);
        resolver.SubjectResolutionCount.ShouldBe(1);

        store.Set(Fragment(7));

        using var entered = new SemaphoreSlim(0);
        using var release = new SemaphoreSlim(0);
        var firstLookup = 0;
        resolver.SubjectResolutionTestHooks = new SubjectResolutionCacheTestHooks
        {
            AfterUpdateLookup = () =>
            {
                if (Interlocked.Exchange(ref firstLookup, 1) != 0)
                {
                    return;
                }

                entered.Release();
                release.Wait();
            },
        };

        var first = RunOnDedicatedThread(() =>
            resolver.ReadAsync(context).AsTask().GetAwaiter().GetResult()
        );

        try
        {
            (await entered.WaitAsync(TimeSpan.FromSeconds(10))).ShouldBeTrue();
            resolver.EvictIdleSubjectResolutionsForTest().ShouldBe(1);

            // A second caller establishes a newer resident generation while the first is still mid-update.
            store.Set(Fragment(11));
            var second = await resolver.ReadAsync(context);
            second.Status.ShouldBe(StateReadStatus.Success);
            resolver.SubjectResolutionCount.ShouldBe(1);
        }
        finally
        {
            release.Release();
        }

        var firstResult = await first;
        firstResult.Status.ShouldBe(StateReadStatus.Success);
        resolver.SubjectResolutionCount.ShouldBe(1);

        using var watch = resolver.GetSourcesForWatch(subject, RouteKey.Default, "fallback");
        watch.Targets.Count.ShouldBe(1);
        // The retried update replaced the newer resident entry's value (revision "2"), proving it was applied.
        watch.Targets[0].ObservedRevision.ShouldBe("2");
    }

    [Test]
    public async Task RepeatedEvictionAndRepopulation_DoesNotDriftTheCacheCount()
    {
        var (_, source) = CreateSource(3);
        var resolver = CreateResolver(source, TimeSpan.Zero);
        var context = source.GetResourceContext(Subject(0));

        for (var iteration = 0; iteration < 500; iteration++)
        {
            _ = await resolver.ReadAsync(context);
            resolver.SubjectResolutionCount.ShouldBe(1);

            resolver.EvictIdleSubjectResolutionsForTest().ShouldBe(1);
            resolver.SubjectResolutionCount.ShouldBe(0);
        }
    }

    [Test]
    public async Task HighConcurrencyStress_KeepsTheCacheBoundedAndConsistent()
    {
        var (_, source) = CreateSource(3);
        var resolver = CreateResolver(source, TimeSpan.Zero, sweepThreshold: 4);
        var subject = Subject(0);
        var context = source.GetResourceContext(subject);

        var negativeCountObserved = 0;
        var maximumCountObserved = 0;
        using var stop = new CancellationTokenSource();
        var sampler = Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                var count = resolver.SubjectResolutionCount;
                if (count < 0)
                {
                    Interlocked.Exchange(ref negativeCountObserved, 1);
                    return;
                }

                if (count > Volatile.Read(ref maximumCountObserved))
                {
                    Volatile.Write(ref maximumCountObserved, count);
                }

                await Task.Yield();
            }
        });

        var workers = new Task[8];
        for (var worker = 0; worker < workers.Length; worker++)
        {
            workers[worker] = Task.Run(async () =>
            {
                for (var iteration = 0; iteration < 250; iteration++)
                {
                    _ = await resolver.ReadAsync(context).ConfigureAwait(false);
                    _ = resolver.EvictIdleSubjectResolutionsForTest();
                    using var watch = resolver.GetSourcesForWatch(
                        subject,
                        RouteKey.Default,
                        "fallback"
                    );
                    _ = watch.Targets.Count;
                }
            });
        }

        await Task.WhenAll(workers);
        await stop.CancelAsync();
        await sampler;

        negativeCountObserved.ShouldBe(0);
        // A single subject key can never account for more than one resident entry.
        maximumCountObserved.ShouldBeLessThanOrEqualTo(1);

        _ = await resolver.ReadAsync(context);
        resolver.SubjectResolutionCount.ShouldBe(1);
        resolver.EvictIdleSubjectResolutionsForTest().ShouldBe(1);
        resolver.SubjectResolutionCount.ShouldBe(0);
    }

    private static Task<T> RunOnDedicatedThread<T>(Func<T> action)
    {
        var completion = new TaskCompletionSource<T>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var thread = new Thread(() =>
        {
            try
            {
                completion.SetResult(action());
            }
            catch (Exception exception)
            {
                completion.SetException(exception);
            }
        })
        {
            IsBackground = true,
        };
        thread.Start();
        return completion.Task;
    }

    private static Task RunOnDedicatedThread(Action action) =>
        RunOnDedicatedThread(() =>
        {
            action();
            return true;
        });

    private static StateSourceResolver<AppSettings.Fragment> CreateResolver(
        StateSource<AppSettings.Fragment> source,
        TimeSpan idleTimeout,
        int sweepThreshold = 1_000_000
    ) =>
        new(
            new StateSourceSet<AppSettings.Fragment>([source]),
            subjectResolutionIdleTimeout: idleTimeout,
            subjectResolutionSweepThreshold: sweepThreshold
        );

    private static (
        InMemoryStateSource<AppSettings.Fragment> Store,
        StateSource<AppSettings.Fragment> Source
    ) CreateSource(int retryCount)
    {
        var store = new InMemoryStateSource<AppSettings.Fragment>(Fragment(retryCount));
        return (
            store,
            new StateSource<AppSettings.Fragment>("cache-source", store, new StateSourceOptions<AppSettings.Fragment> { Writer = store, Watcher = store })
        );
    }

    private static AppSettings.Fragment Fragment(int retryCount) =>
        new() { RetryCount = Optional<int>.Present(retryCount) };

    private static CacheSubject Subject(int index) => new(index);

    private sealed record CacheSubject(int Index) : IConfiglueSubject
    {
        public SubjectKey Key => SubjectKey.From($"cache-subject-{Index}");
    }
}
