using System.Collections.Concurrent;
using Configlue.Sources;
using Configlue.Testing;

namespace Configlue.Tests;

public sealed class SubjectRoutingTests
{
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
                model.Routing<RoutingSubject>(subject =>
                    subject.DataStrict ? RouteKey.From(subject.Region) : RouteKey.Default
                );
                model.Sources(sources =>
                    sources.Add(
                        new StateSource<AppSettings.Fragment>("routed", store, writer: store)
                    )
                );
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
            .ReadContexts.Select(static resourceContext => resourceContext.Key)
            .ShouldAllBe(key => key == subjectKey);
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
    public async Task ResolverAndWatcherKeepRouteInContextWhenSubjectKeyIsUnchanged()
    {
        var subject = new RoutingSubject("unused", true);
        var store = new RoutedStateStore();
        var source = new StateSource<AppSettings.Fragment>(
            "routed",
            store,
            writer: store,
            watcher: store
        );
        store.Set(RouteKey.From("strict-jp"), source.GetSubjectKey(subject), Fragment("tokyo"));
        store.Set(RouteKey.From("strict-eu"), source.GetSubjectKey(subject), Fragment("europe"));
        var resolver = new StateSourceResolver<AppSettings.Fragment>(
            new StateSourceSet<AppSettings.Fragment>([source])
        );

        var tokyoContext = new ConfiglueResourceContext(
            subject,
            source.GetSubjectKey(subject),
            RouteKey.From("strict-jp")
        );
        var europeContext = tokyoContext with { Route = RouteKey.From("strict-eu") };
        (await resolver.ReadAsync(tokyoContext)).Value!.Label.Value.ShouldBe("tokyo");
        (await resolver.ReadAsync(europeContext)).Value!.Label.Value.ShouldBe("europe");
        source.GetResourceId(tokyoContext).ShouldBe(new ResourceId("memory:strict-jp"));
        source.GetResourceId(europeContext).ShouldBe(new ResourceId("memory:strict-eu"));

        var watcher = new StateSourceWatcher<AppSettings.Fragment>(resolver);
        await watcher.WaitForChangeAsync(europeContext, "revision", CancellationToken.None);
        store.LastWatchContext!.Value.Route.ShouldBe(europeContext.Route);
        store.LastWatchContext.Value.Key.ShouldBe(europeContext.Key);
    }

    [Test]
    public async Task CompositeAndFallbackSourcesForwardRouteToComponentOperations()
    {
        var subject = new RoutingSubject("strict-jp", true);
        var route = RouteKey.From("strict-jp");
        var context = new ConfiglueResourceContext(subject, subject.Key, route);

        var fallbackStore = new RoutedStateStore();
        fallbackStore.Set(route, subject.Key, Fragment("fallback-before"));
        var fallbackCandidate = new StateSource<AppSettings.Fragment>(
            "fallback-candidate",
            fallbackStore,
            writer: fallbackStore,
            watcher: fallbackStore
        );
        var fallback = new FallbackStateSource<AppSettings.Fragment>(
            new StateSourceSet<AppSettings.Fragment>([fallbackCandidate])
        );
        var fallbackRead = await fallback.ReadAsync(context);
        fallbackRead.Value!.Label.Value.ShouldBe("fallback-before");
        await fallback.WriteAsync(
            context,
            new StateWriteRequest<AppSettings.Fragment>(
                Fragment("fallback-after"),
                RevisionCondition.FromRevision(fallbackRead.Revision)
            )
        );
        await fallback.WaitForChangeAsync(context, fallbackRead.Revision);
        fallbackStore.LastWriteContext!.Value.Route.ShouldBe(route);
        fallbackStore.LastWatchContext!.Value.Route.ShouldBe(route);

        var compositeStore = new RoutedStateStore();
        compositeStore.Set(route, subject.Key, Fragment("composite"));
        var component = new StateSource<AppSettings.Fragment>(
            "component",
            compositeStore,
            watcher: compositeStore
        );
        var composite = new CompositeStateSource<AppSettings.Fragment>(
            new StateSourceSet<AppSettings.Fragment>([component])
        );
        (await composite.ReadAsync(context)).Value!.Label.Value.ShouldBe("composite");
        await composite.WaitForChangeAsync(context, "revision");
        compositeStore.LastReadContext!.Value.Route.ShouldBe(route);
        compositeStore.LastWatchContext!.Value.Route.ShouldBe(route);
    }

    private static AppSettings.Fragment Fragment(string? label) =>
        new() { Label = Optional<string?>.Present(label) };

    private sealed record RoutingSubject(string Region, bool DataStrict) : IConfiglueSubject
    {
        public SubjectKey Key => SubjectKey.From("same-logical-subject");
    }

    private sealed class RoutedStateStore
        : IContextualSourceReader<AppSettings.Fragment>,
            IContextualSourceWriter<AppSettings.Fragment>,
            IContextualSourceWatcher,
            IContextualResourceIdentity
    {
        private readonly ConcurrentDictionary<
            (SubjectKey Key, RouteKey Route),
            StateReadResult<AppSettings.Fragment>
        > _states = new();

        public ResourceId ResourceId => new("memory:default");

        public ConcurrentQueue<ConfiglueResourceContext> ReadContexts { get; } = new();

        public ConfiglueResourceContext? LastWatchContext { get; private set; }

        public ConfiglueResourceContext? LastWriteContext { get; private set; }

        public ConfiglueResourceContext? LastReadContext { get; private set; }

        public ResourceId GetResourceId(ConfiglueResourceContext context) =>
            new($"memory:{context.Route.Value}");

        public void Set(RouteKey route, SubjectKey key, AppSettings.Fragment value) =>
            _states[(key, route)] = StateReadResult<AppSettings.Fragment>.Success(
                value,
                $"revision:{route.Value}"
            );

        public ValueTask<StateReadResult<AppSettings.Fragment>> ReadAsync(
            CancellationToken cancellationToken = default
        ) => ReadAsync(ConfiglueResourceContext.Default, cancellationToken);

        public ValueTask<StateReadResult<AppSettings.Fragment>> ReadAsync(
            ConfiglueResourceContext context,
            CancellationToken cancellationToken = default
        )
        {
            cancellationToken.ThrowIfCancellationRequested();
            ReadContexts.Enqueue(context);
            LastReadContext = context;
            return ValueTask.FromResult(_states.GetValueOrDefault((context.Key, context.Route)));
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
            _states[(context.Key, context.Route)] = StateReadResult<AppSettings.Fragment>.Success(
                request.Value,
                revision
            );
            return ValueTask.FromResult(new StateWriteResult(revision));
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
            return ValueTask.CompletedTask;
        }
    }
}
