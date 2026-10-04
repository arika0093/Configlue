using System.Collections.Concurrent;
using Configlue;
using Configlue.Resources;
using Configlue.Sources;
using Configlue.Testing;
using Microsoft.Extensions.Logging;

namespace Configlue.Tests;

/// <summary>
/// Contract coverage for the unified product model:
/// state instance = (TModel, StateName), subject = operation scope,
/// profile = catalog-managed named state instance + active selection.
/// </summary>
public sealed class StateInstanceProfileSubjectModelTests
{
    [Test]
    public async Task TwoNamedStatesForSameSubjectAreIndependent()
    {
        await using var context = ConfiglueApp.CreateContext(builder =>
        {
            builder.Add<AppSettings>(model =>
            {
                model.StateName = "game";
                model.Sources(sources => sources.Add(CreateSource("game-source", "game-value")));
            });
            builder.Add<AppSettings>(model =>
            {
                model.StateName = "work";
                model.Sources(sources => sources.Add(CreateSource("work-source", "work-value")));
            });
        });

        // The default instance is not registered; named instances are addressed explicitly.
        Should.Throw<KeyNotFoundException>(() => context.GetState<AppSettings>());

        var subject = new ModelSubject("same-user");
        var game = context.GetSubjectState<AppSettings>("game").ForSubject(subject);
        var work = context.GetSubjectState<AppSettings>("work").ForSubject(subject);

        (await game.GetValueAsync()).Label.ShouldBe("game-value");
        (await work.GetValueAsync()).Label.ShouldBe("work-value");

        await game.SaveAsync(patch =>
        {
            patch.Label = "game-updated";
        });

        // The same subject sees independent values per named state instance.
        (await game.GetValueAsync()).Label.ShouldBe("game-updated");
        (await work.GetValueAsync()).Label.ShouldBe("work-value");
    }

    [Test]
    public async Task TwoSubjectsOfSameNamedStateShareDefinitionButResolveDifferentKeysAndRoutes()
    {
        var store = new SubjectKeyedStore();
        var builder = new StateSourceSetBuilder<AppSettings.Fragment>();
        builder
            .Add("data", store)
            .ResourceKeyBy<ModelSubject>(subject => ResourceKey.From("tenant-" + subject.Tenant))
            .RouteBy<ModelSubject>(subject => RouteKey.From("region-" + subject.Tenant));
        await using var runtime = new ConfiglueRuntime<AppSettings, AppSettings.Fragment>(
            builder.Build(),
            onChangeDebounce: TimeSpan.Zero,
            stateName: "game"
        );
        ISubjectState<AppSettings> subjects = runtime;
        var alice = new ModelSubject("alice");
        var bob = new ModelSubject("bob");

        await subjects
            .ForSubject(alice)
            .SaveAsync(patch =>
            {
                patch.Label = "alice-value";
            });
        await subjects
            .ForSubject(bob)
            .SaveAsync(patch =>
            {
                patch.Label = "bob-value";
            });

        // One shared state definition serves both subjects with per-subject values.
        (await subjects.ForSubject(alice).GetValueAsync()).Label.ShouldBe("alice-value");
        (await subjects.ForSubject(bob).GetValueAsync()).Label.ShouldBe("bob-value");

        var aliceDetails = await subjects.ForSubject(alice).GetDetailsAsync();
        var bobDetails = await subjects.ForSubject(bob).GetDetailsAsync();
        var aliceResolution = aliceDetails.Label.Source!.Resolution!;
        var bobResolution = bobDetails.Label.Source!.Resolution!;

        // Same logical source kind, but subject-specific provider keys, routes, and subject keys.
        aliceDetails.Label.Source!.Kind.ShouldBe(bobDetails.Label.Source!.Kind);
        aliceResolution.ResourceKey.ShouldBe(ResourceKey.From("tenant-alice"));
        bobResolution.ResourceKey.ShouldBe(ResourceKey.From("tenant-bob"));
        aliceResolution.Route.ShouldBe(RouteKey.From("region-alice"));
        bobResolution.Route.ShouldBe(RouteKey.From("region-bob"));
        aliceResolution.ResourceKey.ShouldNotBe(bobResolution.ResourceKey);
        aliceResolution.Route.ShouldNotBe(bobResolution.Route);
        aliceResolution.LogicalSubjectKey.ShouldBe(alice.Key);
        bobResolution.LogicalSubjectKey.ShouldBe(bob.Key);

        // The runtime observed both provider keys on the same state instance.
        store
            .ObservedContexts.Select(static context => context.ResourceKey)
            .ShouldContain(ResourceKey.From("tenant-alice"));
        store
            .ObservedContexts.Select(static context => context.ResourceKey)
            .ShouldContain(ResourceKey.From("tenant-bob"));
    }

    [Test]
    public async Task ProfilesAreNamedStateIdentitiesSurvivingUnloadAndRematerialization()
    {
        var catalogStore = new InMemoryStateSource<ConfiglueProfileCatalog>();
        var catalog = new StateSource<ConfiglueProfileCatalog>(
            "catalog",
            catalogStore,
            new StateSourceOptions<ConfiglueProfileCatalog>
            {
                Writer = catalogStore,
                Watcher = catalogStore,
            }
        );
        // One backing store per state name simulates persisted per-profile data.
        var backingStores = new Dictionary<string, InMemoryStateSource<AppSettings.Fragment>>(
            StringComparer.Ordinal
        );
        StateSource<AppSettings.Fragment> SourceFor(string stateName)
        {
            if (!backingStores.TryGetValue(stateName, out var store))
            {
                store = new InMemoryStateSource<AppSettings.Fragment>(
                    Fragment(stateName + "-value")
                );
                backingStores[stateName] = store;
            }

            return new StateSource<AppSettings.Fragment>(
                "profile-source-" + stateName,
                store,
                new StateSourceOptions<AppSettings.Fragment> { Writer = store, Watcher = store }
            );
        }

        await using var context = ConfiglueApp.CreateContext(builder =>
        {
            builder.Add<AppSettings>(model =>
            {
                model.EnableProfiles(catalog);
                model.ConfigureSources(registration =>
                    registration.Sources.Add(_ => SourceFor(registration.StateName))
                );
            });
        });

        var profiles = context.GetProfiledState<AppSettings>();
        await profiles.GetProfileNamesAsync();
        await profiles.CreateProfileAsync("work", copyFrom: "default");

        var work = await profiles.GetProfileAsync("work");
        await work.SaveAsync(patch =>
        {
            patch.Label = "work-saved";
        });

        // The registry is only a materialization cache: unloading removes the runtime,
        // not the persisted profile membership.
        var registry = context.GetStateRegistry<AppSettings>();
        (await registry.TryRemoveAsync("work")).ShouldBeTrue();
        (await profiles.GetProfileNamesAsync()).ShouldContain("work");

        // Re-materialization restores the same named-state identity with persisted data.
        var rematerialized = await profiles.GetProfileAsync("work");
        (await rematerialized.GetValueAsync()).Label.ShouldBe("work-saved");
        (await context.GetState<AppSettings>("work").GetValueAsync()).Label.ShouldBe("work-saved");
    }

    [Test]
    public async Task ProfilePlusSubjectCompositionIsDeterministic()
    {
        var catalogStore = new InMemoryStateSource<ConfiglueProfileCatalog>();
        var catalog = new StateSource<ConfiglueProfileCatalog>(
            "catalog",
            catalogStore,
            new StateSourceOptions<ConfiglueProfileCatalog>
            {
                Writer = catalogStore,
                Watcher = catalogStore,
            }
        );
        var store = new SubjectKeyedStore();
        await using var context = ConfiglueApp.CreateContext(builder =>
        {
            builder.Add<AppSettings>(model =>
            {
                model.EnableProfiles(catalog);
                model.ConfigureSources(registration =>
                {
                    var stateName = registration.StateName;
                    var routed = new StateSourceSetBuilder<AppSettings.Fragment>();
                    routed
                        .Add("data", store)
                        .ResourceKeyBy<ModelSubject>(subject =>
                            ResourceKey.From(stateName + "/tenant-" + subject.Tenant)
                        );
                    registration.Sources.Add(routed.Build().Sources[0]);
                });
            });
        });

        var profiles = context.GetProfiledState<AppSettings>();
        await profiles.GetProfileNamesAsync();
        await profiles.CreateProfileAsync("game");

        var alice = new ModelSubject("alice");
        var bob = new ModelSubject("bob");
        var game = await profiles.GetProfileAsync("game");
        var gameForAlice = ((ISubjectState<AppSettings>)game).ForSubject(alice);
        var gameForBob = ((ISubjectState<AppSettings>)game).ForSubject(bob);

        var aliceReceipt = await gameForAlice.SaveAsync(patch =>
        {
            patch.Label = "game-alice";
        });
        var bobReceipt = await gameForBob.SaveAsync(patch =>
        {
            patch.Label = "game-bob";
        });

        // Receipts carry the named-state identity plus the subject scope.
        aliceReceipt.StateName.ShouldBe("game");
        aliceReceipt.SubjectKey.ShouldBe(alice.Key);
        bobReceipt.StateName.ShouldBe("game");
        bobReceipt.SubjectKey.ShouldBe(bob.Key);

        // Repeated reads of the same (profile, subject) pair are stable and distinct per subject.
        (await gameForAlice.GetValueAsync()).Label.ShouldBe("game-alice");
        (await gameForBob.GetValueAsync()).Label.ShouldBe("game-bob");
        (await ((ISubjectState<AppSettings>)game).ForSubject(alice).GetValueAsync()).Label.ShouldBe(
            "game-alice"
        );

        // Subject-scoped writes do not leak into server-wide reads of the same instance.
        (await context.GetState<AppSettings>("game").GetValueAsync()).Label.ShouldNotBe(
            "game-alice"
        );
        (await context.GetState<AppSettings>("game").GetValueAsync()).Label.ShouldNotBe("game-bob");
    }

    [Test]
    public async Task DiagnosticsReportStateNameAndSubjectSeparatelyFromSourceAndResourceIdentity()
    {
        var store = new SubjectKeyedStore();
        var builder = new StateSourceSetBuilder<AppSettings.Fragment>();
        builder
            .Add("data", store)
            .ResourceKeyBy<ModelSubject>(subject => ResourceKey.From("tenant-" + subject.Tenant));
        var subjectLog = new SubjectCapturingLogger();
        await using var runtime = new ConfiglueRuntime<AppSettings, AppSettings.Fragment>(
            builder.Build(),
            onChangeDebounce: TimeSpan.Zero,
            stateName: "game",
            logger: subjectLog
        );
        ISubjectState<AppSettings> subjects = runtime;
        IConfiglueDiagnostics<AppSettings> diagnostics = runtime;
        var alice = new ModelSubject("alice");

        var scopedReceipt = await subjects
            .ForSubject(alice)
            .SaveAsync(patch =>
            {
                patch.Label = "alice-value";
            });
        scopedReceipt.StateName.ShouldBe("game");
        scopedReceipt.SubjectKey.ShouldBe(alice.Key);
        scopedReceipt.Sources.Count.ShouldBe(1);
        // The logical source registration is distinct from state-name and subject identity.
        scopedReceipt.Sources[0].SourceId.ShouldBe(SourceId.From("data"));

        var serverReceipt = await runtime.SaveAsync(patch =>
        {
            patch.Label = "server-value";
        });
        serverReceipt.StateName.ShouldBe("game");
        serverReceipt.SubjectKey.ShouldBe(SubjectKey.Default);

        // The compact snapshot reports the last operation; the per-operation subject scope
        // is observed through standard ILogger fields without retaining values.
        var snapshot = diagnostics.GetRuntimeSnapshot();
        snapshot.StateName.ShouldBe("game");
        snapshot.LastWrite!.Value.StateName.ShouldBe("game");
        snapshot.LastWrite!.Value.SubjectKey.ShouldBe(SubjectKey.Default);
        subjectLog.Subjects.ShouldContain(alice.Key.Value);
        subjectLog.Subjects.ShouldContain(SubjectKey.Default.Value);
        subjectLog.States.ShouldAllBe(static state => state == "game");
    }

    private sealed class SubjectCapturingLogger : ILogger
    {
        internal List<string> Subjects { get; } = [];
        internal List<string?> States { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter
        )
        {
            if (state is not IEnumerable<KeyValuePair<string, object?>> fields)
                return;
            string? stateName = null;
            string? subject = null;
            foreach (var field in fields)
            {
                if (field.Key == "StateName")
                    stateName = field.Value as string;
                if (field.Key == "SubjectKey")
                    subject = field.Value as string;
            }
            States.Add(stateName);
            if (subject is not null)
                Subjects.Add(subject);
        }
    }

    private static StateSource<AppSettings.Fragment> CreateSource(string id, string label)
    {
        var store = new InMemoryStateSource<AppSettings.Fragment>(Fragment(label));
        return new StateSource<AppSettings.Fragment>(
            id,
            store,
            new StateSourceOptions<AppSettings.Fragment> { Writer = store, Watcher = store }
        );
    }

    private static AppSettings.Fragment Fragment(string? label) =>
        new() { Label = Optional<string?>.Present(label) };

    private sealed record ModelSubject(string Tenant) : IConfiglueSubject
    {
        public SubjectKey Key => SubjectKey.FromSegments(Tenant);
    }

    private sealed class SubjectKeyedStore
        : ISourceReader<AppSettings.Fragment>,
            ISourceWriter<AppSettings.Fragment>,
            ISourceWatcher
    {
        private readonly ConcurrentDictionary<
            ResourceKey,
            InMemoryStateSource<AppSettings.Fragment>
        > _states = new();
        private readonly ConcurrentBag<ConfiglueResourceContext> _observedContexts = [];

        public IReadOnlyCollection<ConfiglueResourceContext> ObservedContexts =>
            _observedContexts.ToArray();

        public ValueTask<StateReadResult<AppSettings.Fragment>> ReadAsync(
            CancellationToken cancellationToken = default
        ) => Get(ResourceKey.Default).ReadAsync(cancellationToken);

        public ValueTask<StateReadResult<AppSettings.Fragment>> ReadAsync(
            ConfiglueResourceContext context,
            CancellationToken cancellationToken = default
        )
        {
            _observedContexts.Add(context);
            return Get(context.ResourceKey).ReadAsync(cancellationToken);
        }

        public ValueTask<StateWriteResult> WriteAsync(
            StateWriteRequest<AppSettings.Fragment> request,
            CancellationToken cancellationToken = default
        ) => Get(ResourceKey.Default).WriteAsync(request, cancellationToken);

        public ValueTask<StateWriteResult> WriteAsync(
            ConfiglueResourceContext context,
            StateWriteRequest<AppSettings.Fragment> request,
            CancellationToken cancellationToken = default
        )
        {
            _observedContexts.Add(context);
            return Get(context.ResourceKey).WriteAsync(request, cancellationToken);
        }

        public ValueTask WaitForChangeAsync(
            string? observedRevision,
            CancellationToken cancellationToken = default
        ) => Get(ResourceKey.Default).WaitForChangeAsync(observedRevision, cancellationToken);

        public ValueTask WaitForChangeAsync(
            ConfiglueResourceContext context,
            string? observedRevision,
            CancellationToken cancellationToken = default
        )
        {
            _observedContexts.Add(context);
            return Get(context.ResourceKey).WaitForChangeAsync(observedRevision, cancellationToken);
        }

        private InMemoryStateSource<AppSettings.Fragment> Get(ResourceKey key) =>
            _states.GetOrAdd(key, static _ => new InMemoryStateSource<AppSettings.Fragment>());
    }
}
