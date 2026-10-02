using System.Collections.Concurrent;
using Configlue.Sources;
using Configlue.Testing;

namespace Configlue.Tests;

public sealed class SubjectRoutingTests
{
    [Test]
    public void SourceResourceKeyAndRouteAreIndependentFromApplicationSubjectIdentity()
    {
        var subject = new RoutingSubject("tenant-a", true);
        var resourceKey = ResourceKey.From("provider-record-7");
        var route = RouteKey.From("jp");
        var source = new StateSource<AppSettings.Fragment>(
            "tenant-settings",
            new InMemoryStateSource<AppSettings.Fragment>(),
            resourceKeySelector: _ => resourceKey,
            routeSelector: _ => route
        );

        var context = source.GetResourceContext(subject);

        context.Subject.Key.ShouldBe(subject.Key);
        context.ResourceKey.ShouldBe(resourceKey);
        context.Route.ShouldBe(route);
        source.GetResourceKey(subject).ShouldBe(resourceKey);
    }

    [Test]
    public async Task ModelRoutingKeepsLogicalKeysSeparateFromDefaultAndPhysicalRoutes()
    {
        var subjectKey = SubjectKey.From("same-logical-subject");
        var store = new RoutedStateStore();
        store.Set(RouteKey.Default, subjectKey, Fragment("default"));
        store.Set(RouteKey.From("strict-jp"), subjectKey, Fragment("tokyo"));
        store.Set(RouteKey.From("strict-eu"), subjectKey, Fragment("europe"));

        await using var context = ConfiglueApp.CreateContext(builder =>
        {
            builder.Add<AppSettings>(model =>
            {
                model.Sources(sources =>
                {
                    var routed = new StateSourceSetBuilder<AppSettings.Fragment>();
                    routed
                        .Add("routed", store)
                        .RouteBy<RoutingSubject>(subject =>
                            subject.DataStrict
                                ? RouteKey.From(subject.Region)
                                : RouteKey.Default
                        );
                    sources.Add(routed.Build().Sources[0]);
                });
            });
        });

        var options = context.GetSubjectState<AppSettings>();
        var defaultValue = await options
            .ForSubject(new RoutingSubject("default", false))
            .GetValueAsync();
        var tokyoValue = await options
            .ForSubject(new RoutingSubject("strict-jp", true))
            .GetValueAsync();
        var europeValue = await options
            .ForSubject(new RoutingSubject("strict-eu", true))
            .GetValueAsync();

        defaultValue.Label.ShouldBe("default");
        tokyoValue.Label.ShouldBe("tokyo");
        europeValue.Label.ShouldBe("europe");
        var tokyoReceipt = await options
            .ForSubject(new RoutingSubject("strict-jp", true))
            .SaveAsync(
                new AppSettings.Patch { Label = FragmentOperation<string?>.Set("tokyo-updated") }
            );
        store.LastWriteContext!.Value.Route.ShouldBe(RouteKey.From("strict-jp"));
        tokyoReceipt.Sources.Single().ResourceId.ShouldBe(new ResourceId("memory:strict-jp"));
        store
            .ReadContexts.Select(static resourceContext => resourceContext.ResourceKey)
            .ShouldAllBe(key => key == ResourceKey.From(subjectKey));
        store
            .ReadContexts.Select(static resourceContext => resourceContext.Route)
            .ShouldContain(RouteKey.Default);
        store
            .ReadContexts.Select(static resourceContext => resourceContext.Route)
            .ShouldContain(RouteKey.From("strict-jp"));
        store
            .ReadContexts.Select(static resourceContext => resourceContext.Route)
            .ShouldContain(RouteKey.From("strict-eu"));
    }

    [Test]
    public async Task SourcesOnOneModelCanUseDifferentRoutePolicies()
    {
        var subject = new RoutingSubject("strict-jp", true);
        var labelStore = new RoutedStateStore();
        var retryStore = new RoutedStateStore();
        labelStore.Set(RouteKey.From("strict-jp"), subject.Key, Fragment("japan"));
        retryStore.Set(RouteKey.From("strict-eu"), subject.Key, FragmentWithRetry(7));
        await using var context = ConfiglueApp.CreateContext(builder =>
        {
            builder.Add<AppSettings>(model =>
                model.Sources(sources =>
                {
                    var first = new StateSourceSetBuilder<AppSettings.Fragment>();
                    first
                        .Add("first", labelStore)
                        .RouteBy<RoutingSubject>(_ => RouteKey.From("strict-jp"))
                        .WithoutWriter();
                    var second = new StateSourceSetBuilder<AppSettings.Fragment>();
                    second
                        .Add("second", retryStore)
                        .RouteBy<RoutingSubject>(_ => RouteKey.From("strict-eu"))
                        .WithoutWriter();
                    sources.Add(first.Build().Sources[0]);
                    sources.Add(second.Build().Sources[0]);
                })
            );
        });

        var value = await context.GetSubjectState<AppSettings>().ForSubject(subject).GetValueAsync();

        value.Label.ShouldBe("japan");
        value.RetryCount.ShouldBe(7);
        labelStore
            .ReadContexts.Select(static context => context.Route)
            .ShouldAllBe(route => route == RouteKey.From("strict-jp"));
        retryStore
            .ReadContexts.Select(static context => context.Route)
            .ShouldAllBe(route => route == RouteKey.From("strict-eu"));
    }

    [Test]
    public async Task SubjectDetailsExposeResolvedRoutingAndPlacement()
    {
        var subject = new RoutingSubject("strict-jp", true);
        var store = new RoutedStateStore();
        store.Set(RouteKey.From("strict-jp"), subject.Key, Fragment("tokyo"));
        await using var context = CreateRoutedContext(store);

        var details = await context
            .GetSubjectState<AppSettings>()
            .ForSubject(subject)
            .GetDetailsAsync();

        var resolution = details.Label.Source?.Resolution;
        resolution.ShouldNotBeNull();
        (resolution!.LogicalSubjectKey).ShouldBe(subject.Key);
        (resolution.ResourceKey).ShouldBe(ResourceKey.From(subject.Key));
        (resolution.Route).ShouldBe(RouteKey.From("strict-jp"));
        (resolution.ResourceId).ShouldBe(new ResourceId("memory:strict-jp"));
        (resolution.PhysicalOrigin).ShouldBe("memory:strict-jp");
    }

    [Test]
    public async Task SubjectDetailsDifferPerResolvedPhysicalLocation()
    {
        var japan = new RoutingSubject("strict-jp", true);
        var europe = new RoutingSubject("strict-eu", true);
        var store = new RoutedStateStore();
        store.Set(RouteKey.From("strict-jp"), japan.Key, Fragment("tokyo"));
        store.Set(RouteKey.From("strict-eu"), europe.Key, Fragment("europe"));
        await using var context = CreateRoutedContext(store);
        var options = context.GetSubjectState<AppSettings>();

        var japanDetails = await options.ForSubject(japan).GetDetailsAsync();
        var europeDetails = await options.ForSubject(europe).GetDetailsAsync();

        var japanResolution = japanDetails.Label.Source?.Resolution;
        var europeResolution = europeDetails.Label.Source?.Resolution;
        japanResolution.ShouldNotBeNull();
        europeResolution.ShouldNotBeNull();
        (japanResolution!.Route).ShouldBe(RouteKey.From("strict-jp"));
        (europeResolution!.Route).ShouldBe(RouteKey.From("strict-eu"));
        (japanResolution.ResourceId).ShouldBe(new ResourceId("memory:strict-jp"));
        (europeResolution.ResourceId).ShouldBe(new ResourceId("memory:strict-eu"));
        (japanResolution.PhysicalOrigin).ShouldBe("memory:strict-jp");
        (europeResolution.PhysicalOrigin).ShouldBe("memory:strict-eu");
    }

    [Test]
    public async Task SubjectDetailsResolveRoutingOnceWithoutExtraReads()
    {
        var routeCalls = 0;
        var subject = new RoutingSubject("strict-jp", true);
        var store = new RoutedStateStore();
        store.Set(RouteKey.From("strict-jp"), subject.Key, Fragment("tokyo"));
        await using var context = ConfiglueApp.CreateContext(builder =>
        {
            builder.Add<AppSettings>(model =>
            {
                model.Sources(sources =>
                {
                    var first = new StateSourceSetBuilder<AppSettings.Fragment>();
                    first
                        .Add("first", store)
                        .RouteBy<RoutingSubject>(candidate =>
                        {
                            Interlocked.Increment(ref routeCalls);
                            return candidate.DataStrict
                                ? RouteKey.From(candidate.Region)
                                : RouteKey.Default;
                        });
                    sources.Add(first.Build().Sources[0]);
                    sources.Add(new StateSource<AppSettings.Fragment>("second", store));
                });
            });
        });

        var details = await context
            .GetSubjectState<AppSettings>()
            .ForSubject(subject)
            .GetDetailsAsync();

        (details.Label.Source?.Resolution).ShouldNotBeNull();
        Volatile.Read(ref routeCalls).ShouldBe(1);
        store.ReadCount.ShouldBe(2);
    }

    private static ConfiglueContext CreateRoutedContext(RoutedStateStore store) =>
        ConfiglueApp.CreateContext(builder =>
        {
            builder.Add<AppSettings>(model =>
            {
                model.Sources(sources =>
                {
                    var routed = new StateSourceSetBuilder<AppSettings.Fragment>();
                    routed
                        .Add("routed", store)
                        .RouteBy<RoutingSubject>(candidate =>
                            candidate.DataStrict
                                ? RouteKey.From(candidate.Region)
                                : RouteKey.Default
                        );
                    sources.Add(routed.Build().Sources[0]);
                });
            });
        });

    [Test]
    public async Task ResolverAndWatcherKeepRouteInContextWhenSubjectKeyIsUnchanged()
    {
        var japan = new RoutingSubject("strict-jp", true);
        var europe = new RoutingSubject("strict-eu", true);
        var store = new RoutedStateStore();
        var builder = new StateSourceSetBuilder<AppSettings.Fragment>();
        builder
            .Add("routed", store)
            .RouteBy<RoutingSubject>(candidate =>
                candidate.DataStrict ? RouteKey.From(candidate.Region) : RouteKey.Default
            );
        var source = builder.Build().Sources[0];
        store.Set(RouteKey.From("strict-jp"), source.GetResourceKey(japan), Fragment("tokyo"));
        store.Set(RouteKey.From("strict-eu"), source.GetResourceKey(europe), Fragment("europe"));
        var resolver = new StateSourceResolver<AppSettings.Fragment>(
            new StateSourceSet<AppSettings.Fragment>([source])
        );

        var tokyoContext = source.GetResourceContext(japan);
        var europeContext = source.GetResourceContext(europe);
        (await resolver.ReadAsync(tokyoContext)).Value!.Label.Value.ShouldBe("tokyo");
        (await resolver.ReadAsync(europeContext)).Value!.Label.Value.ShouldBe("europe");
        source.GetResourceId(tokyoContext).ShouldBe(new ResourceId("memory:strict-jp"));
        source.GetResourceId(europeContext).ShouldBe(new ResourceId("memory:strict-eu"));

        var watcher = new StateSourceWatcher<AppSettings.Fragment>(resolver);
        await watcher.WaitForChangeAsync(europeContext, "revision", CancellationToken.None);
        store.LastWatchContext!.Value.Route.ShouldBe(europeContext.Route);
        store.LastWatchContext.Value.ResourceKey.ShouldBe(europeContext.ResourceKey);
    }

    [Test]
    public async Task ResolverWatcherUsesEffectiveContextCapturedWithObservedRevision()
    {
        var subject = new MutableRoutingSubject("old-key", "jp");
        var fallbackStore = new RoutedStateStore();
        var activeStore = new RoutedStateStore();
        var oldResourceKey = ResourceKey.From("old-key");
        var oldRoute = RouteKey.From("jp");
        fallbackStore.SetNotFound(oldRoute, oldResourceKey, "missing:jp");
        activeStore.Set(oldRoute, oldResourceKey, Fragment("japan"));
        var fallbackSource = new StateSource<AppSettings.Fragment>(
            "fallback",
            fallbackStore,
            priority: 10,
            fallbackCondition: StateFallbackCondition.NotFound,
            watcher: fallbackStore,
            resourceKeySelector: candidate => ResourceKey.From(((MutableRoutingSubject)candidate).Resource),
            routeSelector: candidate => RouteKey.From(((MutableRoutingSubject)candidate).Region)
        );
        var source = new StateSource<AppSettings.Fragment>(
            "mutable-route",
            activeStore,
            watcher: activeStore,
            resourceKeySelector: candidate => ResourceKey.From(((MutableRoutingSubject)candidate).Resource),
            routeSelector: candidate => RouteKey.From(((MutableRoutingSubject)candidate).Region)
        );
        var resolver = new StateSourceResolver<AppSettings.Fragment>(
            new StateSourceSet<AppSettings.Fragment>([fallbackSource, source])
        );
        var outerContext = new ConfiglueResourceContext(
            subject,
            ResourceKey.From("outer-key"),
            RouteKey.From("outer-route")
        );

        (await resolver.ReadAsync(outerContext)).Revision.ShouldBe("revision:jp");
        subject.Resource = "new-key";
        subject.Region = "eu";

        await new StateSourceWatcher<AppSettings.Fragment>(resolver).WaitForChangeAsync(
            outerContext,
            "fallback-revision"
        );

        fallbackStore.LastWatchContext!.Value.ResourceKey.ShouldBe(oldResourceKey);
        fallbackStore.LastWatchContext.Value.Route.ShouldBe(oldRoute);
        fallbackStore.LastObservedRevision.ShouldBe("missing:jp");
        activeStore.LastWatchContext!.Value.ResourceKey.ShouldBe(oldResourceKey);
        activeStore.LastWatchContext.Value.Route.ShouldBe(oldRoute);
        activeStore.LastObservedRevision.ShouldBe("revision:jp");
    }

    [Test]
    public async Task CompositeWatcherUsesEachComponentContextCapturedDuringRead()
    {
        var subject = new MutableRoutingSubject("label-key", "jp")
        {
            SecondaryResource = "retry-key",
            SecondaryRegion = "us",
        };
        var labelStore = new RoutedStateStore();
        var retryStore = new RoutedStateStore();
        var label = new StateSource<AppSettings.Fragment>(
            "label",
            labelStore,
            watcher: labelStore,
            resourceKeySelector: candidate => ResourceKey.From(((MutableRoutingSubject)candidate).Resource),
            routeSelector: candidate => RouteKey.From(((MutableRoutingSubject)candidate).Region)
        );
        var retry = new StateSource<AppSettings.Fragment>(
            "retry",
            retryStore,
            watcher: retryStore,
            resourceKeySelector: candidate => ResourceKey.From(((MutableRoutingSubject)candidate).SecondaryResource),
            routeSelector: candidate => RouteKey.From(((MutableRoutingSubject)candidate).SecondaryRegion)
        );
        labelStore.Set(RouteKey.From("jp"), ResourceKey.From("label-key"), Fragment("japan"));
        retryStore.Set(
            RouteKey.From("us"),
            ResourceKey.From("retry-key"),
            FragmentWithRetry(4)
        );
        var composite = new CompositeStateSource<AppSettings.Fragment>(
            new StateSourceSet<AppSettings.Fragment>([label, retry])
        );
        var outerContext = new ConfiglueResourceContext(
            subject,
            ResourceKey.From("outer-key"),
            RouteKey.From("outer-route")
        );

        (await composite.ReadAsync(outerContext)).Status.ShouldBe(StateReadStatus.Success);
        subject.Resource = "changed-label";
        subject.Region = "eu";
        subject.SecondaryResource = "changed-retry";
        subject.SecondaryRegion = "ca";

        await composite.WaitForChangeAsync(outerContext, "composite-revision");

        labelStore.LastWatchContext!.Value.ResourceKey.ShouldBe(ResourceKey.From("label-key"));
        labelStore.LastWatchContext.Value.Route.ShouldBe(RouteKey.From("jp"));
        labelStore.LastObservedRevision.ShouldBe("revision:jp");
        retryStore.LastWatchContext!.Value.ResourceKey.ShouldBe(ResourceKey.From("retry-key"));
        retryStore.LastWatchContext.Value.Route.ShouldBe(RouteKey.From("us"));
        retryStore.LastObservedRevision.ShouldBe("revision:us");
    }

    [Test]
    public async Task CompositeAndFallbackSourcesForwardRouteToComponentOperations()
    {
        var subject = new RoutingSubject("strict-jp", true);
        var route = RouteKey.From("strict-jp");

        var fallbackStore = new RoutedStateStore();
        fallbackStore.Set(route, subject.Key, Fragment("fallback-before"));
        var fallbackBuilder = new StateSourceSetBuilder<AppSettings.Fragment>();
        fallbackBuilder
            .Add("fallback-candidate", fallbackStore)
            .RouteBy<RoutingSubject>(candidate =>
                candidate.DataStrict ? RouteKey.From(candidate.Region) : RouteKey.Default
            );
        var fallbackCandidate = fallbackBuilder.Build().Sources[0];
        var fallback = new FallbackStateSource<AppSettings.Fragment>(
            new StateSourceSet<AppSettings.Fragment>([fallbackCandidate])
        );
        var fallbackContext = fallbackCandidate.GetResourceContext(subject);
        var fallbackRead = await fallback.ReadAsync(fallbackContext);
        fallbackRead.Value!.Label.Value.ShouldBe("fallback-before");
        await fallback.WriteAsync(
            fallbackContext,
            new StateWriteRequest<AppSettings.Fragment>(
                Fragment("fallback-after"),
                RevisionCondition.FromRevision(fallbackRead.Revision)
            )
        );
        await fallback.WaitForChangeAsync(fallbackContext, fallbackRead.Revision);
        fallbackStore.LastWriteContext!.Value.Route.ShouldBe(route);
        fallbackStore.LastWatchContext!.Value.Route.ShouldBe(route);

        var compositeStore = new RoutedStateStore();
        compositeStore.Set(route, subject.Key, Fragment("composite"));
        var compositeBuilder = new StateSourceSetBuilder<AppSettings.Fragment>();
        compositeBuilder
            .Add("component", compositeStore)
            .RouteBy<RoutingSubject>(candidate =>
                candidate.DataStrict ? RouteKey.From(candidate.Region) : RouteKey.Default
            );
        var component = compositeBuilder.Build().Sources[0];
        var composite = new CompositeStateSource<AppSettings.Fragment>(
            new StateSourceSet<AppSettings.Fragment>([component])
        );
        var compositeContext = component.GetResourceContext(subject);
        (await composite.ReadAsync(compositeContext)).Value!.Label.Value.ShouldBe("composite");
        await composite.WaitForChangeAsync(compositeContext, "revision");
        compositeStore.LastReadContext!.Value.Route.ShouldBe(route);
        compositeStore.LastWatchContext!.Value.Route.ShouldBe(route);
    }

    [Test]
    public async Task FallbackStateSource_RoutesConcurrentSubjectsToTheirResolvedCandidates()
    {
        var japan = new RoutingSubject("strict-jp", true);
        var europe = new RoutingSubject("strict-eu", true);
        var canonicalStore = new RoutedStateStore();
        var legacyStore = new RoutedStateStore();
        var canonicalBuilder = new StateSourceSetBuilder<AppSettings.Fragment>();
        canonicalBuilder
            .Add("canonical", canonicalStore)
            .RouteBy<RoutingSubject>(subject => RouteKey.From(subject.Region));
        var legacyBuilder = new StateSourceSetBuilder<AppSettings.Fragment>();
        legacyBuilder
            .Add("legacy", legacyStore)
            .RouteBy<RoutingSubject>(subject => RouteKey.From(subject.Region));
        var canonical = canonicalBuilder.Build().Sources[0];
        var legacy = legacyBuilder.Build().Sources[0];
        var japanContext = canonical.GetResourceContext(japan);
        var europeContext = canonical.GetResourceContext(europe);
        canonicalStore.SetNotFound(japanContext.Route, japanContext.ResourceKey);
        canonicalStore.Set(europeContext.Route, europeContext.ResourceKey, Fragment("europe-before"));
        legacyStore.Set(japanContext.Route, japanContext.ResourceKey, Fragment("japan-before"));
        legacyStore.SetNotFound(europeContext.Route, europeContext.ResourceKey);

        var fallback = new FallbackStateSource<AppSettings.Fragment>(
            new StateSourceSet<AppSettings.Fragment>([canonical, legacy])
        );

        var japanRead = await fallback.ReadAsync(japanContext);
        var europeRead = await fallback.ReadAsync(europeContext);
        japanRead.SourceId.ShouldBe("legacy");
        europeRead.SourceId.ShouldBe("canonical");

        await fallback.WriteAsync(
            japanContext,
            new StateWriteRequest<AppSettings.Fragment>(
                Fragment("japan-after"),
                RevisionCondition.FromRevision(japanRead.Revision)
            )
        );
        await fallback.WriteAsync(
            europeContext,
            new StateWriteRequest<AppSettings.Fragment>(
                Fragment("europe-after"),
                RevisionCondition.FromRevision(europeRead.Revision)
            )
        );

        (await legacyStore.ReadAsync(japanContext)).Value!.Label.Value.ShouldBe("japan-after");
        (await canonicalStore.ReadAsync(europeContext)).Value!.Label.Value.ShouldBe("europe-after");
        (await canonicalStore.ReadAsync(japanContext)).Status.ShouldBe(StateReadStatus.NotFound);
        (await legacyStore.ReadAsync(europeContext)).Status.ShouldBe(StateReadStatus.NotFound);
    }

    private static AppSettings.Fragment Fragment(string? label) =>
        new() { Label = Optional<string?>.Present(label) };

    private static AppSettings.Fragment FragmentWithRetry(int retryCount) =>
        new() { RetryCount = Optional<int>.Present(retryCount) };

    private sealed record RoutingSubject(string Region, bool DataStrict) : IConfiglueSubject
    {
        public SubjectKey Key => SubjectKey.From("same-logical-subject");
    }

    private sealed class MutableRoutingSubject(string resource, string region) : IConfiglueSubject
    {
        public string Resource { get; set; } = resource;
        public string Region { get; set; } = region;
        public string SecondaryResource { get; set; } = resource;
        public string SecondaryRegion { get; set; } = region;

        public SubjectKey Key => SubjectKey.From("same-logical-subject");
    }

    private sealed class RoutedStateStore
        : ISourceReader<AppSettings.Fragment>,
            ISourceWriter<AppSettings.Fragment>,
            ISourceWatcher,
            IResourceIdentity
    {
        private readonly ConcurrentDictionary<
            (ResourceKey Key, RouteKey Route),
            StateReadResult<AppSettings.Fragment>
        > _states = new();
        private int _readCount;

        public ResourceId ResourceId => new("memory:default");

        public int ReadCount => Volatile.Read(ref _readCount);

        public ConcurrentQueue<ConfiglueResourceContext> ReadContexts { get; } = new();

        public ConfiglueResourceContext? LastWatchContext { get; private set; }

        public ConfiglueResourceContext? LastWriteContext { get; private set; }

        public ConfiglueResourceContext? LastReadContext { get; private set; }

        public string? LastObservedRevision { get; private set; }

        public ResourceId GetResourceId(ConfiglueResourceContext context) =>
            new($"memory:{context.Route.Value}");

        public void Set(RouteKey route, SubjectKey key, AppSettings.Fragment value) =>
            Set(route, ResourceKey.From(key), value);

        public void Set(RouteKey route, ResourceKey key, AppSettings.Fragment value) =>
            _states[(key, route)] = StateReadResult<AppSettings.Fragment>.Success(
                value,
                $"revision:{route.Value}"
            );

        public void SetNotFound(RouteKey route, ResourceKey key, string? revision = null) =>
            _states[(key, route)] = StateReadResult<AppSettings.Fragment>.NotFound(revision);

        public ValueTask<StateReadResult<AppSettings.Fragment>> ReadAsync(
            CancellationToken cancellationToken = default
        ) => ReadAsync(ConfiglueResourceContext.Default, cancellationToken);

        public ValueTask<StateReadResult<AppSettings.Fragment>> ReadAsync(
            ConfiglueResourceContext context,
            CancellationToken cancellationToken = default
        )
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _readCount);
            ReadContexts.Enqueue(context);
            LastReadContext = context;
            return ValueTaskCompat.FromResult(
                _states.GetValueOrDefault((context.ResourceKey, context.Route)) with
                {
                    PhysicalOrigin = $"memory:{context.Route.Value}",
                }
            );
        }

        public ValueTask<StateWriteResult> WriteAsync(
            StateWriteRequest<AppSettings.Fragment> request,
            CancellationToken cancellationToken = default
        ) => WriteAsync(ConfiglueResourceContext.Default, request, cancellationToken);

        public ValueTask<StateWriteResult> WriteAsync(
            ConfiglueResourceContext context,
            StateWriteRequest<AppSettings.Fragment> request,
            CancellationToken cancellationToken = default
        )
        {
            cancellationToken.ThrowIfCancellationRequested();
            LastWriteContext = context;
            var revision = $"revision:{context.Route.Value}:{Guid.NewGuid():N}";
            _states[(context.ResourceKey, context.Route)] = StateReadResult<AppSettings.Fragment>.Success(
                request.Value,
                revision
            );
            return ValueTaskCompat.FromResult(new StateWriteResult(revision));
        }

        public ValueTask WaitForChangeAsync(
            string? observedRevision,
            CancellationToken cancellationToken = default
        ) => ValueTask.CompletedTask;

        public ValueTask WaitForChangeAsync(
            ConfiglueResourceContext context,
            string? observedRevision,
            CancellationToken cancellationToken = default
        )
        {
            LastWatchContext = context;
            LastObservedRevision = observedRevision;
            return ValueTask.CompletedTask;
        }
    }
}
