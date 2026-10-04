using BenchmarkDotNet.Attributes;
using Configlue;
using Configlue.CompilerServices;
using Configlue.Sources;
using Configlue.State;
using Configlue.Testing;

[ConfiglueModel("bench-nested-routing-220", Version = 1)]
public partial class NestedWriteRouting220Settings
{
    public string Name { get; set; } = string.Empty;

    public int RetryCount { get; set; }

    public NestedWriteRouting220Database Database { get; set; } = new();

    public NestedWriteRouting220Cache Cache { get; set; } = new();
}

[ConfiglueModel("bench-nested-routing-220-db", Version = 1)]
public partial class NestedWriteRouting220Database
{
    public string Host { get; set; } = "localhost";

    public int Port { get; set; } = 5432;

    public string Username { get; set; } = "user";

    public int TimeoutSeconds { get; set; } = 30;

    public bool UseTls { get; set; }

    public int PoolSize { get; set; } = 10;

    public string DatabaseName { get; set; } = "app";
}

[ConfiglueModel("bench-nested-routing-220-cache", Version = 1)]
public partial class NestedWriteRouting220Cache
{
    public string Endpoint { get; set; } = "cache.local";

    public int Port { get; set; } = 6379;

    public int TtlSeconds { get; set; } = 60;

    public bool Enabled { get; set; } = true;

    public string Region { get; set; } = "local";

    public int MaxEntries { get; set; } = 1000;

    public int ShardCount { get; set; } = 1;
}

/// <summary>
/// Nested generated write-routing benchmarks for issue #220 (extends #211).
/// </summary>
/// <remarks>
/// <para>
/// #211 covers flat 1/4/16-member routing. This group adds nested routed paths so
/// the recursive partitioning hot path is measured: every nested change resolves
/// ownership through longest-prefix <see cref="ConfiglueMemberPath"/> lookups
/// (<c>ResolveSourceIdOrNull</c>/<c>HasRouteBelow</c> compiled overloads) with no
/// per-member <c>string.Join(".", path)</c> construction and no
/// <c>FromNames</c> (<c>Split</c> + name lookup) re-resolution.
/// </para>
/// <para>
/// The three benchmarks run production routing paths:
/// <see cref="RouteNestedPatch"/> calls the generated
/// <see cref="IConfiglueRoutablePatch.Route"/> partition directly (nested
/// <c>RouteCore</c> recursion), <see cref="SaveNestedRoutedAsync"/> saves through a
/// two-source longest-prefix plan, and <see cref="CommitNestedCompositeAsync"/>
/// commits an edit session through a <see cref="CompositeStateSource{TFragment}"/>
/// so the composite <c>PartitionCompositeChanges</c> recursion runs. Storage I/O is
/// pinned to <see cref="InMemoryStateSource{T}"/> so routing allocations are not
/// hidden behind I/O.
/// </para>
/// <para>
/// To confirm the group regresses when string round trips are restored, route the
/// composite partition through dotted strings again (for example,
/// <c>path.Add(member.Name)</c> plus <c>string.Join(".", path)</c> per member with
/// the string <c>ResolveWriteComponent</c>/<c>HasWriteRouteBelow</c> overloads, or
/// recompile schema-bound lookups via <c>ConfiglueMemberPath.FromNames</c> per
/// member), re-run this filter on the same machine, and compare the
/// <c>Allocated</c> column: the reverted run allocates one joined string per
/// member per nesting level plus one split/name-lookup pass per resolution on top
/// of the baseline. Restore the generated-ID path afterwards.
/// </para>
/// </remarks>
[MemoryDiagnoser]
public class NestedWriteRoutingBenchmarks220
{
    private StateWritePlan _routePlan = null!;
    private SourceId _fallback;
    private NestedWriteRouting220Settings.Patch _patch = null!;
    private ConfiglueContext _routedContext = null!;
    private IWritableState<NestedWriteRouting220Settings> _routedState = null!;
    private ConfiglueContext _compositeContext = null!;
    private bool _commitFlip;

    [Params(1, 4, 16)]
    public int PresentMemberCount { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        var left = SourceKey<NestedWriteRouting220Settings>.Named("left");
        var right = SourceKey<NestedWriteRouting220Settings>.Named("right");
        // Longest-prefix ownership inside each nested object: the nested root goes
        // to one source while one leaf is overridden to the other source.
        _routePlan = StateWritePlan
            .For<NestedWriteRouting220Settings>()
            .DefaultTo(left)
            .Route(x => x.Name, left)
            .Route(x => x.RetryCount, right)
            .Route(x => x.Database, left)
            .Route(x => x.Database.Port, right)
            .Route(x => x.Database.Host, left)
            .Route(x => x.Database.Username, left)
            .Route(x => x.Database.TimeoutSeconds, left)
            .Route(x => x.Database.UseTls, left)
            .Route(x => x.Database.PoolSize, left)
            .Route(x => x.Database.DatabaseName, left)
            .Route(x => x.Cache, right)
            .Route(x => x.Cache.Port, left)
            .Route(x => x.Cache.Endpoint, right)
            .Route(x => x.Cache.TtlSeconds, right)
            .Route(x => x.Cache.Enabled, right)
            .Route(x => x.Cache.Region, right)
            .Route(x => x.Cache.MaxEntries, right)
            .Route(x => x.Cache.ShardCount, right)
            .Build();
        _fallback = SourceId.From("left");
        _patch = CreatePatch(PresentMemberCount);

        var leftStore = new InMemoryStateSource<NestedWriteRouting220Settings.Fragment>(
            CreateLeftSeed()
        );
        var rightStore = new InMemoryStateSource<NestedWriteRouting220Settings.Fragment>(
            CreateRightSeed()
        );
        _routedContext = BenchmarkContextFactory.Create<
            NestedWriteRouting220Settings,
            NestedWriteRouting220Settings.Fragment
        >(
            new StateSourceSet<NestedWriteRouting220Settings.Fragment>([
                new StateSource<NestedWriteRouting220Settings.Fragment>(
                    "left",
                    leftStore,
                    new StateSourceOptions<NestedWriteRouting220Settings.Fragment>
                    {
                        Priority = 100,
                        Writer = leftStore,
                    }
                ),
                new StateSource<NestedWriteRouting220Settings.Fragment>(
                    "right",
                    rightStore,
                    new StateSourceOptions<NestedWriteRouting220Settings.Fragment>
                    {
                        Priority = 50,
                        Writer = rightStore,
                    }
                ),
            ]),
            model => model.WritePlan = _routePlan
        );
        _routedState = _routedContext.GetState<NestedWriteRouting220Settings>();

        var compositeStore = new InMemoryStateSource<NestedWriteRouting220Settings.Fragment>(
            CreateFullSeed()
        );
        // NOTE: a single composite component mirrors the existing composite write tests.
        // Nested partitioning still recurses per nested fragment and resolves each
        // nested member against the bound composite plan; a second component would
        // only add an unrelated revision-baseline failure (see benchmarks README).
        var composite = new CompositeStateSource<NestedWriteRouting220Settings.Fragment>(
            new StateSourceSet<NestedWriteRouting220Settings.Fragment>([
                new StateSource<NestedWriteRouting220Settings.Fragment>(
                    "child",
                    compositeStore,
                    new StateSourceOptions<NestedWriteRouting220Settings.Fragment>
                    {
                        Writer = compositeStore,
                        Watcher = compositeStore,
                    }
                ),
            ]),
            writePlan: StateWritePlan
                .For<NestedWriteRouting220Settings>()
                .Route(x => x.Name, SourceKey<NestedWriteRouting220Settings>.Named("child"))
                .Route(x => x.RetryCount, SourceKey<NestedWriteRouting220Settings>.Named("child"))
                .Route(x => x.Database, SourceKey<NestedWriteRouting220Settings>.Named("child"))
                .Route(
                    x => x.Database.Port,
                    SourceKey<NestedWriteRouting220Settings>.Named("child")
                )
                .Route(x => x.Cache, SourceKey<NestedWriteRouting220Settings>.Named("child"))
                .Route(x => x.Cache.Port, SourceKey<NestedWriteRouting220Settings>.Named("child"))
                .Build()
        );
        _compositeContext = BenchmarkContextFactory.Create<
            NestedWriteRouting220Settings,
            NestedWriteRouting220Settings.Fragment
        >(
            new StateSourceSet<NestedWriteRouting220Settings.Fragment>([
                composite.CreateSource("combined"),
            ])
        );
    }

    [GlobalCleanup]
    public async Task CleanupAsync()
    {
        await _routedContext.DisposeAsync().ConfigureAwait(false);
        await _compositeContext.DisposeAsync().ConfigureAwait(false);
    }

    [Benchmark(Description = "Nested generated typed patch route partition")]
    public int RouteNestedPatch()
    {
        var routed = ((IConfiglueRoutablePatch)_patch).Route(_routePlan, _fallback);
        return routed.Count;
    }

    [Benchmark(Description = "Nested multi-source routed save with in-memory stores")]
    public async Task SaveNestedRoutedAsync()
    {
        await _routedState.SaveAsync(_patch).ConfigureAwait(false);
    }

    [Benchmark(Description = "Nested composite edit-session commit with in-memory store")]
    public async Task CommitNestedCompositeAsync()
    {
        // Alternate values every iteration so the edit always diffs into nested
        // fragment changes and the composite partition recursion runs.
        _commitFlip = !_commitFlip;
        var sessions = _compositeContext.GetEditSessions<NestedWriteRouting220Settings>();
        using var edit = await sessions.OpenEditSessionAsync().ConfigureAwait(false);
        ApplyNestedValues(edit.Value, _commitFlip, PresentMemberCount);
        await edit.CommitAsync().ConfigureAwait(false);
    }

    private static void ApplyNestedValues(
        NestedWriteRouting220Settings value,
        bool flip,
        int presentCount
    )
    {
        var leaf = 0;
        if (presentCount <= leaf++)
        {
            return;
        }

        value.Name = flip ? "benchmark-routing-a" : "benchmark-routing-b";
        if (presentCount <= leaf++)
        {
            return;
        }

        value.RetryCount = flip ? 7 : 9;
        if (presentCount <= leaf++)
        {
            return;
        }

        value.Database.Host = flip ? "db-a.local" : "db-b.local";
        if (presentCount <= leaf++)
        {
            return;
        }

        // Longest-prefix override leaf: owned by the opposite source of Database.
        value.Database.Port = flip ? 5432 : 5433;
        if (presentCount <= leaf++)
        {
            return;
        }

        value.Database.Username = flip ? "user-a" : "user-b";
        if (presentCount <= leaf++)
        {
            return;
        }

        value.Database.TimeoutSeconds = flip ? 30 : 31;
        if (presentCount <= leaf++)
        {
            return;
        }

        value.Database.UseTls = flip;
        if (presentCount <= leaf++)
        {
            return;
        }

        value.Database.PoolSize = flip ? 10 : 11;
        if (presentCount <= leaf++)
        {
            return;
        }

        value.Database.DatabaseName = flip ? "app-a" : "app-b";
        if (presentCount <= leaf++)
        {
            return;
        }

        value.Cache.Endpoint = flip ? "cache-a.local" : "cache-b.local";
        if (presentCount <= leaf++)
        {
            return;
        }

        // Longest-prefix override leaf: owned by the opposite source of Cache.
        value.Cache.Port = flip ? 6379 : 6380;
        if (presentCount <= leaf++)
        {
            return;
        }

        value.Cache.TtlSeconds = flip ? 60 : 61;
        if (presentCount <= leaf++)
        {
            return;
        }

        value.Cache.Enabled = flip;
        if (presentCount <= leaf++)
        {
            return;
        }

        value.Cache.Region = flip ? "region-a" : "region-b";
        if (presentCount <= leaf++)
        {
            return;
        }

        value.Cache.MaxEntries = flip ? 1000 : 1001;
        if (presentCount <= leaf++)
        {
            return;
        }

        value.Cache.ShardCount = flip ? 1 : 2;
    }

    private static NestedWriteRouting220Settings.Patch CreatePatch(int presentCount)
    {
        var patch = new NestedWriteRouting220Settings.Patch();
        var leaf = 0;
        if (presentCount <= leaf++)
        {
            return patch;
        }

        patch.Name = "benchmark-routing";
        if (presentCount <= leaf++)
        {
            return patch;
        }

        patch.RetryCount = 7;
        if (presentCount <= leaf++)
        {
            return patch;
        }

        var databasePatch = new NestedWriteRouting220Database.Patch { Host = "db.local" };
        patch.Database = databasePatch;
        if (presentCount <= leaf++)
        {
            return patch;
        }

        databasePatch.Port = 5432;
        if (presentCount <= leaf++)
        {
            return patch;
        }

        databasePatch.Username = "user";
        if (presentCount <= leaf++)
        {
            return patch;
        }

        databasePatch.TimeoutSeconds = 30;
        if (presentCount <= leaf++)
        {
            return patch;
        }

        databasePatch.UseTls = true;
        if (presentCount <= leaf++)
        {
            return patch;
        }

        databasePatch.PoolSize = 10;
        if (presentCount <= leaf++)
        {
            return patch;
        }

        databasePatch.DatabaseName = "app";
        if (presentCount <= leaf++)
        {
            return patch;
        }

        var cachePatch = new NestedWriteRouting220Cache.Patch { Endpoint = "cache.local" };
        patch.Cache = cachePatch;
        if (presentCount <= leaf++)
        {
            return patch;
        }

        cachePatch.Port = 6379;
        if (presentCount <= leaf++)
        {
            return patch;
        }

        cachePatch.TtlSeconds = 60;
        if (presentCount <= leaf++)
        {
            return patch;
        }

        cachePatch.Enabled = true;
        if (presentCount <= leaf++)
        {
            return patch;
        }

        cachePatch.Region = "region";
        if (presentCount <= leaf++)
        {
            return patch;
        }

        cachePatch.MaxEntries = 1000;
        if (presentCount <= leaf++)
        {
            return patch;
        }

        cachePatch.ShardCount = 2;
        return patch;
    }

    private static NestedWriteRouting220Settings.Fragment CreateLeftSeed() =>
        new()
        {
            Name = Optional<string>.Present("seed"),
            Database = Optional<NestedWriteRouting220Database.Fragment?>.Present(
                new NestedWriteRouting220Database.Fragment
                {
                    Host = Optional<string>.Present("db.local"),
                    Username = Optional<string>.Present("user"),
                    TimeoutSeconds = Optional<int>.Present(30),
                    UseTls = Optional<bool>.Present(false),
                    PoolSize = Optional<int>.Present(10),
                    DatabaseName = Optional<string>.Present("app"),
                }
            ),
            Cache = Optional<NestedWriteRouting220Cache.Fragment?>.Present(
                new NestedWriteRouting220Cache.Fragment { Port = Optional<int>.Present(6379) }
            ),
        };

    private static NestedWriteRouting220Settings.Fragment CreateRightSeed() =>
        new()
        {
            RetryCount = Optional<int>.Present(3),
            Database = Optional<NestedWriteRouting220Database.Fragment?>.Present(
                new NestedWriteRouting220Database.Fragment { Port = Optional<int>.Present(5432) }
            ),
            Cache = Optional<NestedWriteRouting220Cache.Fragment?>.Present(
                new NestedWriteRouting220Cache.Fragment
                {
                    Endpoint = Optional<string>.Present("cache.local"),
                    TtlSeconds = Optional<int>.Present(60),
                    Enabled = Optional<bool>.Present(true),
                    Region = Optional<string>.Present("local"),
                    MaxEntries = Optional<int>.Present(1000),
                    ShardCount = Optional<int>.Present(1),
                }
            ),
        };

    private static NestedWriteRouting220Settings.Fragment CreateFullSeed() =>
        new()
        {
            Name = Optional<string>.Present("seed"),
            RetryCount = Optional<int>.Present(3),
            Database = Optional<NestedWriteRouting220Database.Fragment?>.Present(
                new NestedWriteRouting220Database.Fragment
                {
                    Host = Optional<string>.Present("db.local"),
                    Port = Optional<int>.Present(5432),
                    Username = Optional<string>.Present("user"),
                    TimeoutSeconds = Optional<int>.Present(30),
                    UseTls = Optional<bool>.Present(false),
                    PoolSize = Optional<int>.Present(10),
                    DatabaseName = Optional<string>.Present("app"),
                }
            ),
            Cache = Optional<NestedWriteRouting220Cache.Fragment?>.Present(
                new NestedWriteRouting220Cache.Fragment
                {
                    Endpoint = Optional<string>.Present("cache.local"),
                    Port = Optional<int>.Present(6379),
                    TtlSeconds = Optional<int>.Present(60),
                    Enabled = Optional<bool>.Present(true),
                    Region = Optional<string>.Present("local"),
                    MaxEntries = Optional<int>.Present(1000),
                    ShardCount = Optional<int>.Present(1),
                }
            ),
        };
}
