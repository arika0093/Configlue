using Configlue.Resources;
using Configlue.Sources;
using Configlue.State;
using Configlue.Testing;

namespace Configlue.Tests;

public sealed class ResolverRetainedWatchTests
{
    [Test]
    public async Task SingleSource_StableReads_ReuseInlineTopologyWithoutArray()
    {
        var store = new InMemoryStateSource<AppSettings.Fragment>(
            new AppSettings.Fragment { RetryCount = Optional<int>.Present(3) }
        );
        var source = new StateSource<AppSettings.Fragment>(
            "single",
            store,
            writer: store,
            watcher: store
        );
        var resolver = new StateSourceResolver<AppSettings.Fragment>(
            new StateSourceSet<AppSettings.Fragment>([source])
        );

        await resolver.ReadAsync(ConfiglueResourceContext.Default);
        var first = resolver.GetWatchTopologyForTest(null, RouteKey.Default);
        first.ShouldNotBeNull();
        first!.Count.ShouldBe(1);
        first.UsesRetainedArray.ShouldBeFalse();

        await resolver.ReadAsync(ConfiglueResourceContext.Default);
        var second = resolver.GetWatchTopologyForTest(null, RouteKey.Default);
        second.ShouldBeSameAs(first);

        using var watch = resolver.GetSourcesForWatch("fallback");
        watch.Targets.Count.ShouldBe(1);
        watch.Targets[0].ObservedRevision.ShouldBe("1");
    }

    [Test]
    public async Task MultiSource_StableTopology_ReusedWhenOnlyRevisionsChange()
    {
        var (resolver, stores) = CreateLastWinsResolver(4);
        await resolver.ReadAsync(ConfiglueResourceContext.Default);
        var first = resolver.GetWatchTopologyForTest(null, RouteKey.Default);
        first.ShouldNotBeNull();
        first!.Count.ShouldBe(4);

        // Only the observed revision changes; source/context routing is unchanged.
        stores[3].Set(new AppSettings.Fragment { RetryCount = Optional<int>.Present(9) });

        await resolver.ReadAsync(ConfiglueResourceContext.Default);
        var second = resolver.GetWatchTopologyForTest(null, RouteKey.Default);
        second.ShouldBeSameAs(first);

        using var watch = resolver.GetSourcesForWatch("fallback");
        watch.Targets.Count.ShouldBe(4);
        watch.Targets[3].ObservedRevision.ShouldBe("2");
    }

    [Test]
    public async Task MultiSource_FailoverToEarlierSource_RebuildsTopology()
    {
        var first = new InMemoryStateSource<AppSettings.Fragment>();
        var last = new InMemoryStateSource<AppSettings.Fragment>(
            new AppSettings.Fragment { RetryCount = Optional<int>.Present(1) }
        );
        var sources = new StateSourceSet<AppSettings.Fragment>([
            new StateSource<AppSettings.Fragment>(
                "first",
                first,
                new StateSourceOptions<AppSettings.Fragment>
                {
                    Priority = 2,
                    FallbackCondition = StateFallbackCondition.NotFound,
                }
            ),
            new StateSource<AppSettings.Fragment>(
                "last",
                last,
                new StateSourceOptions<AppSettings.Fragment>
                {
                    Priority = 1,
                    FallbackCondition = StateFallbackCondition.NotFound,
                }
            ),
        ]);
        var resolver = new StateSourceResolver<AppSettings.Fragment>(sources);

        await resolver.ReadAsync(ConfiglueResourceContext.Default);
        var deepTopology = resolver.GetWatchTopologyForTest(null, RouteKey.Default);
        deepTopology.ShouldNotBeNull();
        deepTopology!.Count.ShouldBe(2);

        // Failover: the higher-priority source now succeeds, so fewer sources are inspected.
        first.Set(new AppSettings.Fragment { RetryCount = Optional<int>.Present(7) });
        var result = await resolver.ReadAsync(ConfiglueResourceContext.Default);
        result.Status.ShouldBe(StateReadStatus.Success);
        result.Value!.RetryCount.ShouldBe(7);

        var shallowTopology = resolver.GetWatchTopologyForTest(null, RouteKey.Default);
        shallowTopology.ShouldNotBeNull();
        shallowTopology!.Count.ShouldBe(1);
        shallowTopology.ShouldNotBeSameAs(deepTopology);

        using var watch = resolver.GetSourcesForWatch("fallback");
        watch.Targets.Count.ShouldBe(1);
        watch.Targets[0].Source.Id.ShouldBe(SourceId.From("first"));
    }

    [Test]
    public async Task SubjectRouting_TopologiesAreIndependentAndReusedPerSubject()
    {
        var store = new InMemoryStateSource<AppSettings.Fragment>(
            new AppSettings.Fragment { RetryCount = Optional<int>.Present(3) }
        );
        var source = new StateSource<AppSettings.Fragment>(
            "subject-source",
            store,
            writer: store,
            watcher: store
        );
        var resolver = new StateSourceResolver<AppSettings.Fragment>(
            new StateSourceSet<AppSettings.Fragment>([source])
        );
        var first = new WatchSubject("first");
        var second = new WatchSubject("second");

        await resolver.ReadAsync(source.GetResourceContext(first));
        await resolver.ReadAsync(source.GetResourceContext(second));
        var firstTopology = resolver.GetWatchTopologyForTest(first, RouteKey.Default);
        var secondTopology = resolver.GetWatchTopologyForTest(second, RouteKey.Default);
        firstTopology.ShouldNotBeNull();
        secondTopology.ShouldNotBeNull();
        secondTopology.ShouldNotBeSameAs(firstTopology);

        await resolver.ReadAsync(source.GetResourceContext(first));
        resolver
            .GetWatchTopologyForTest(first, RouteKey.Default)
            .ShouldBeSameAs(firstTopology);
        resolver
            .GetWatchTopologyForTest(second, RouteKey.Default)
            .ShouldBeSameAs(secondTopology);

        using var watch = resolver.GetSourcesForWatch(first, RouteKey.Default, "fallback");
        watch.Targets.Count.ShouldBe(1);
        watch.Targets[0].ObservedRevision.ShouldBe("1");
    }

    private static (
        StateSourceResolver<AppSettings.Fragment> Resolver,
        InMemoryStateSource<AppSettings.Fragment>[] Stores
    ) CreateLastWinsResolver(int sourceCount)
    {
        var stores = new InMemoryStateSource<AppSettings.Fragment>[sourceCount];
        var sources = new StateSource<AppSettings.Fragment>[sourceCount];
        for (var index = 0; index < sourceCount; index++)
        {
            stores[index] =
                index == sourceCount - 1
                    ? new InMemoryStateSource<AppSettings.Fragment>(
                        new AppSettings.Fragment
                        {
                            RetryCount = Optional<int>.Present(index),
                        }
                    )
                    : new InMemoryStateSource<AppSettings.Fragment>();
            sources[index] = new StateSource<AppSettings.Fragment>(
                $"retained-{index}",
                stores[index],
                new StateSourceOptions<AppSettings.Fragment>
                {
                    Priority = sourceCount - index,
                    FallbackCondition = StateFallbackCondition.NotFound,
                }
            );
        }

        return (
            new StateSourceResolver<AppSettings.Fragment>(
                new StateSourceSet<AppSettings.Fragment>(sources)
            ),
            stores
        );
    }

    private sealed record WatchSubject(string Name) : IConfiglueSubject
    {
        public SubjectKey Key => SubjectKey.From(Name);
    }
}
