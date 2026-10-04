using System.ComponentModel.DataAnnotations;
using Configlue.Testing;

namespace Configlue.Tests;

[ConfiglueModel("runtime-coordinator-settings", Version = 1)]
public partial class RuntimeCoordinatorSettings
{
    [Required]
    public string? Label { get; set; }
}

public sealed partial class RuntimeCoordinatorTests
{
    private static RuntimeLifetime CreateLifetime() => new();

    private static RuntimeSourceTopology<RuntimeCoordinatorSettings.Fragment> CreateTopology(
        params StateSource<RuntimeCoordinatorSettings.Fragment>[] sources
    ) => new(new StateSourceSet<RuntimeCoordinatorSettings.Fragment>(sources), "coordinator-tests");

    private static StateSource<RuntimeCoordinatorSettings.Fragment> CreateSource(
        string id,
        InMemoryStateSource<RuntimeCoordinatorSettings.Fragment> store,
        bool writable = false
    ) =>
        writable
            ? new StateSource<RuntimeCoordinatorSettings.Fragment>(
                id,
                store,
                new StateSourceOptions<RuntimeCoordinatorSettings.Fragment> { Writer = store }
            )
            : new StateSource<RuntimeCoordinatorSettings.Fragment>(
                id,
                store,
                new StateSourceOptions<RuntimeCoordinatorSettings.Fragment>()
            );

    private static RuntimeResolutionEngine<
        RuntimeCoordinatorSettings,
        RuntimeCoordinatorSettings.Fragment
    > CreateEngine(RuntimeSourceTopology<RuntimeCoordinatorSettings.Fragment> topology)
    {
        var subjects = new RuntimeSubjectContext();
        var recorder = new RuntimeDiagnosticRecorder(
            "coordinator-tests",
            "runtime-coordinator-settings",
            1,
            ConfiglueRuntimeDiagnosticOptions.Default,
            []
        );
        var validation = new RuntimeValidationPipeline<
            RuntimeCoordinatorSettings,
            RuntimeCoordinatorSettings.Fragment
        >([], validateDataAnnotations: true, "coordinator-tests", recorder);
        return new RuntimeResolutionEngine<
            RuntimeCoordinatorSettings,
            RuntimeCoordinatorSettings.Fragment
        >(
            topology,
            subjects,
            recorder,
            CreateLifetime(),
            validation,
            new RuntimeModelCloner<RuntimeCoordinatorSettings, RuntimeCoordinatorSettings.Fragment>(
                cloneStrategy: null
            ),
            migrations: null,
            readValidationMode: ReadValidationMode.EffectiveThrow
        );
    }

    [Test]
    public async Task Lifetime_Shutdown_DrainsActiveOperations_Once()
    {
        var lifetime = CreateLifetime();
        var lease = lifetime.EnterOperation();
        var first = lifetime.TryBeginShutdown(out var drained);
        first.ShouldBeTrue();
        drained.IsCompleted.ShouldBeFalse();
        lease.Dispose();
        await drained.WaitAsync(TimeSpan.FromSeconds(5));
        var second = lifetime.TryBeginShutdown(out var drainedAgain);
        second.ShouldBeFalse();
        drainedAgain.IsCompleted.ShouldBeTrue();
    }

    [Test]
    public void Lifetime_Registration_AfterShutdown_Throws_ButUnregisterIsAllowed()
    {
        var lifetime = CreateLifetime();
        lifetime.TryBeginShutdown(out _).ShouldBeTrue();
        Should.Throw<ObjectDisposedException>(() => lifetime.EnterOperation());
        Should.Throw<ObjectDisposedException>(() => lifetime.Register(() => 42));
        var listeners = new List<Action>();
        var copy = lifetime.TrySnapshot(listeners, out var snapshot);
        copy.ShouldBeFalse();
        snapshot.ShouldBeEmpty();
        Should.NotThrow(() => lifetime.Unregister(() => listeners.Add(() => { })));
        listeners.Count.ShouldBe(1);
    }

    [Test]
    public void SubjectContext_Enter_RestoresPreviousSubject()
    {
        var subjects = new RuntimeSubjectContext();
        subjects.Current.ShouldBeNull();
        subjects.CurrentKey.ShouldBe(SubjectKey.Default);
        var first = new CoordinatorSubject("first");
        var second = new CoordinatorSubject("second");
        using (subjects.Enter(first))
        {
            subjects.Current.ShouldBe(first);
            subjects.CurrentKey.ShouldBe(first.Key);
            using (subjects.Enter(second))
            {
                subjects.Current.ShouldBe(second);
            }

            subjects.Current.ShouldBe(first);
        }

        subjects.Current.ShouldBeNull();
    }

    [Test]
    public void Topology_FindSource_ResolvesRegisteredSources()
    {
        var store = new InMemoryStateSource<RuntimeCoordinatorSettings.Fragment>();
        var topology = CreateTopology(CreateSource("one", store));
        topology.FindSource(SourceId.From("one")).Id.ShouldBe(SourceId.From("one"));
        Should.Throw<InvalidOperationException>(() =>
            topology.FindSource(SourceId.From("missing"))
        );
    }

    [Test]
    public void Topology_RetireSources_PublishesNewSnapshot_AndSignals()
    {
        var firstStore = new InMemoryStateSource<RuntimeCoordinatorSettings.Fragment>();
        var secondStore = new InMemoryStateSource<RuntimeCoordinatorSettings.Fragment>();
        var topology = CreateTopology(
            CreateSource("one", firstStore),
            CreateSource("two", secondStore)
        );
        var before = topology.TopologyChangedTask;
        before.IsCompleted.ShouldBeFalse();

        var active = topology.RetireSources([SourceId.From("two")]);
        active.ShouldNotBeNull();
        active!.Select(static source => source.Id).ShouldBe([SourceId.From("one")]);
        topology.GetActiveSources().Length.ShouldBe(1);
        topology.IsSourceActive(SourceId.From("two")).ShouldBeFalse();
        topology.IsSourceActive(SourceId.From("one")).ShouldBeTrue();
        before.IsCompleted.ShouldBeTrue();
        topology.TopologyChangedTask.IsCompleted.ShouldBeFalse();

        topology.RetireSources([SourceId.From("two")]).ShouldBeNull();
    }

    [Test]
    public void Topology_DetailsSourceKeys_AreStablePerSource()
    {
        var store = new InMemoryStateSource<RuntimeCoordinatorSettings.Fragment>();
        var topology = CreateTopology(CreateSource("one", store));
        var first = topology.GetDetailsSourceKey(SourceId.From("one"));
        topology.GetDetailsSourceKey(SourceId.From("one")).ShouldBe(first);
        topology.GetDetailsSourceKey(SourceId.From("two")).ShouldNotBe(first);
    }

    [Test]
    public void Topology_SingleSourceFastPath_RequiresPlainWritableRoot()
    {
        var store = new InMemoryStateSource<RuntimeCoordinatorSettings.Fragment>(
            new RuntimeCoordinatorSettings.Fragment { Label = "only" }
        );
        var topology = CreateTopology(CreateSource("one", store, writable: true));
        var plan = StateWritePlan.Empty.WithDefaultSourceId(SourceId.From("one"));

        topology.TryEnableSingleSourceFastPath(plan, migrationCount: 0);
        topology.IsSingleSourceFastPath.ShouldBeTrue();
        topology.FastPathWriteSource.ShouldNotBeNull();
        topology.FastPathWriteSource!.Id.ShouldBe(SourceId.From("one"));

        var withMigrations = CreateTopology(CreateSource("one", store, writable: true));
        withMigrations.TryEnableSingleSourceFastPath(plan, migrationCount: 1);
        withMigrations.IsSingleSourceFastPath.ShouldBeFalse();
    }

    [Test]
    public void Topology_SingleSourceFastPath_Disabled_ForMultipleSources()
    {
        var first = new InMemoryStateSource<RuntimeCoordinatorSettings.Fragment>();
        var second = new InMemoryStateSource<RuntimeCoordinatorSettings.Fragment>();
        var topology = CreateTopology(
            CreateSource("one", first, writable: true),
            CreateSource("two", second, writable: true)
        );
        var plan = StateWritePlan.Empty.WithDefaultSourceId(SourceId.From("one"));
        topology.TryEnableSingleSourceFastPath(plan, migrationCount: 0);
        topology.IsSingleSourceFastPath.ShouldBeFalse();
    }

    [Test]
    public void ValidationPipeline_Validate_AcceptsValid_RejectsMissingRequired()
    {
        var recorder = new RuntimeDiagnosticRecorder(
            "coordinator-tests",
            "runtime-coordinator-settings",
            1,
            ConfiglueRuntimeDiagnosticOptions.Default,
            []
        );
        var pipeline = new RuntimeValidationPipeline<
            RuntimeCoordinatorSettings,
            RuntimeCoordinatorSettings.Fragment
        >([], validateDataAnnotations: true, "coordinator-tests", recorder);

        Should.NotThrow(() => pipeline.Validate(new RuntimeCoordinatorSettings { Label = "ok" }));
        Should.Throw<ConfiglueValidationException>(() =>
            pipeline.Validate(new RuntimeCoordinatorSettings { Label = null })
        );
    }

    [Test]
    public async Task ResolutionEngine_ResolvesLayeredSources_HigherPriorityWins()
    {
        var high = new StubReader(
            StateReadResult<RuntimeCoordinatorSettings.Fragment>.Success(
                new RuntimeCoordinatorSettings.Fragment { Label = "high" },
                "rev-high"
            )
        );
        var low = new StubReader(
            StateReadResult<RuntimeCoordinatorSettings.Fragment>.Success(
                new RuntimeCoordinatorSettings.Fragment { Label = "low" },
                "rev-low"
            )
        );
        var topology = CreateTopology(
            new StateSource<RuntimeCoordinatorSettings.Fragment>(
                "high",
                high,
                new StateSourceOptions<RuntimeCoordinatorSettings.Fragment> { Priority = 2 }
            ),
            new StateSource<RuntimeCoordinatorSettings.Fragment>(
                "low",
                low,
                new StateSourceOptions<RuntimeCoordinatorSettings.Fragment> { Priority = 1 }
            )
        );
        var engine = CreateEngine(topology);

        var resolved = await engine.ResolveAsync(null, CancellationToken.None);
        resolved.Result.Status.ShouldBe(StateReadStatus.Success);
        resolved.Result.Value!.Label.ShouldBe("high");
        resolved
            .Result.Revisions!.TryGetRevision(SourceId.From("high"), out var highRevision)
            .ShouldBeTrue();
        highRevision.ShouldBe("rev-high");
        resolved
            .Result.Revisions!.TryGetRevision(SourceId.From("low"), out var lowRevision)
            .ShouldBeTrue();
        lowRevision.ShouldBe("rev-low");
    }

    private sealed record CoordinatorSubject(string Name) : IConfiglueSubject
    {
        public SubjectKey Key => SubjectKey.From(Name);
    }

    private sealed class StubReader(StateReadResult<RuntimeCoordinatorSettings.Fragment> result)
        : ISourceReader<RuntimeCoordinatorSettings.Fragment>
    {
        public ValueTask<StateReadResult<RuntimeCoordinatorSettings.Fragment>> ReadAsync(
            ConfiglueResourceContext context,
            CancellationToken cancellationToken = default
        )
        {
            _ = context;
            _ = cancellationToken;
            return ValueTaskCompat.FromResult(result);
        }
    }
}
