using BenchmarkDotNet.Attributes;
using Configlue;
using Configlue.CompilerServices;
using Configlue.Sources;
using Configlue.State;
using Configlue.Testing;

public enum GeneratedWriteRoutingLevel
{
    Debug = 0,
    Information = 1,
    Warning = 2,
    Error = 3,
}

[ConfiglueModel("bench-generated-write-routing", Version = 1)]
public partial class GeneratedWriteRoutingSettings
{
    public int IntCounter { get; set; }

    public bool BoolFlag { get; set; }

    public long LongValue { get; set; }

    public double DoubleRatio { get; set; }

    public GeneratedWriteRoutingLevel Level { get; set; }

    public DayOfWeek Day { get; set; }

    public int? NullableInt { get; set; }

    public bool? NullableBool { get; set; }

    public GeneratedWriteRoutingLevel? NullableLevel { get; set; }

    public Guid RequestId { get; set; }

    public Guid? NullableRequestId { get; set; }

    public DateTime Timestamp { get; set; }

    public DateTime? NullableTimestamp { get; set; }

    public TimeSpan Timeout { get; set; }

    public TimeSpan? NullableTimeout { get; set; }

    public string Name { get; set; } = string.Empty;
}

/// <summary>
/// Generated write-routing allocation benchmarks for issue #211 (follow-up of #170).
/// </summary>
/// <remarks>
/// The 16-member value-type-heavy model above exercises the generated routing surface with
/// ints, bools, enums, nullable value types, and structs. <see cref="PresentMemberCount"/>
/// builds sparse 1/4/16-member patches in setup, and every benchmark below runs a production
/// write-routing path: <see cref="RoutePatch"/> calls the generated
/// <see cref="IConfiglueRoutablePatch.Route"/> partition directly, <see cref="SaveRoutedAsync"/>
/// saves through a two-source routed plan, and <see cref="SaveCompositeAsync"/> saves through
/// a <see cref="CompositeStateSource{TFragment}"/> so the composite component-patch path
/// (<c>PrepareCompositePatchAsync</c> via the generated <c>Route</c>) runs.
/// Storage I/O is pinned to <see cref="InMemoryStateSource{T}"/> so reintroducing
/// <c>object?</c> boxing, iterator allocations, or LINQ materialization in the routing path
/// shows up as increased allocated bytes. Plain <c>Merge</c>/<c>ToModel</c> measurements are
/// intentionally not used here because they bypass the routing hot path.
/// </remarks>
[MemoryDiagnoser]
public class GeneratedWriteRoutingBenchmarks
{
    private static readonly string[] LeftMemberNames =
    [
        nameof(GeneratedWriteRoutingSettings.IntCounter),
        nameof(GeneratedWriteRoutingSettings.LongValue),
        nameof(GeneratedWriteRoutingSettings.Level),
        nameof(GeneratedWriteRoutingSettings.NullableInt),
        nameof(GeneratedWriteRoutingSettings.NullableLevel),
        nameof(GeneratedWriteRoutingSettings.NullableRequestId),
        nameof(GeneratedWriteRoutingSettings.NullableTimestamp),
        nameof(GeneratedWriteRoutingSettings.NullableTimeout),
    ];

    private static readonly string[] RightMemberNames =
    [
        nameof(GeneratedWriteRoutingSettings.BoolFlag),
        nameof(GeneratedWriteRoutingSettings.DoubleRatio),
        nameof(GeneratedWriteRoutingSettings.Day),
        nameof(GeneratedWriteRoutingSettings.NullableBool),
        nameof(GeneratedWriteRoutingSettings.RequestId),
        nameof(GeneratedWriteRoutingSettings.Timestamp),
        nameof(GeneratedWriteRoutingSettings.Timeout),
        nameof(GeneratedWriteRoutingSettings.Name),
    ];

    private StateWritePlan _routePlan = null!;
    private SourceId _fallback;
    private GeneratedWriteRoutingSettings.Patch _patch = null!;
    private ConfiglueContext _routedContext = null!;
    private IWritableState<GeneratedWriteRoutingSettings> _routedState = null!;
    private ConfiglueContext _compositeContext = null!;
    private IWritableState<GeneratedWriteRoutingSettings> _compositeState = null!;

    [Params(1, 4, 16)]
    public int PresentMemberCount { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        var left = SourceKey<GeneratedWriteRoutingSettings>.Named("left");
        var right = SourceKey<GeneratedWriteRoutingSettings>.Named("right");
        var builder = StateWritePlan
            .For<GeneratedWriteRoutingSettings>()
            .DefaultTo(left)
            .Route(x => x.IntCounter, left)
            .Route(x => x.BoolFlag, right)
            .Route(x => x.LongValue, left)
            .Route(x => x.DoubleRatio, right)
            .Route(x => x.Level, left)
            .Route(x => x.Day, right)
            .Route(x => x.NullableInt, left)
            .Route(x => x.NullableBool, right)
            .Route(x => x.NullableLevel, left)
            .Route(x => x.RequestId, right)
            .Route(x => x.NullableRequestId, left)
            .Route(x => x.Timestamp, right)
            .Route(x => x.NullableTimestamp, left)
            .Route(x => x.Timeout, right)
            .Route(x => x.NullableTimeout, left)
            .Route(x => x.Name, right);
        _routePlan = builder.Build();
        _fallback = SourceId.From("left");
        _patch = CreatePatch(PresentMemberCount);

        var leftStore = new InMemoryStateSource<GeneratedWriteRoutingSettings.Fragment>(
            CreateLeftSeed()
        );
        var rightStore = new InMemoryStateSource<GeneratedWriteRoutingSettings.Fragment>(
            CreateRightSeed()
        );
        _routedContext = BenchmarkContextFactory.Create<
            GeneratedWriteRoutingSettings,
            GeneratedWriteRoutingSettings.Fragment
        >(
            new StateSourceSet<GeneratedWriteRoutingSettings.Fragment>([
                new StateSource<GeneratedWriteRoutingSettings.Fragment>(
                    "left",
                    leftStore,
                    new StateSourceOptions<GeneratedWriteRoutingSettings.Fragment>
                    {
                        Priority = 100,
                        Writer = leftStore,
                    }
                ),
                new StateSource<GeneratedWriteRoutingSettings.Fragment>(
                    "right",
                    rightStore,
                    new StateSourceOptions<GeneratedWriteRoutingSettings.Fragment>
                    {
                        Priority = 50,
                        Writer = rightStore,
                    }
                ),
            ]),
            model => model.WritePlan = _routePlan
        );
        _routedState = _routedContext.GetState<GeneratedWriteRoutingSettings>();

        var leftCompositeStore = new InMemoryStateSource<GeneratedWriteRoutingSettings.Fragment>(
            CreateLeftSeed()
        );
        var rightCompositeStore = new InMemoryStateSource<GeneratedWriteRoutingSettings.Fragment>(
            CreateRightSeed()
        );
        var composite = new CompositeStateSource<GeneratedWriteRoutingSettings.Fragment>(
            new StateSourceSet<GeneratedWriteRoutingSettings.Fragment>([
                new StateSource<GeneratedWriteRoutingSettings.Fragment>(
                    "left",
                    leftCompositeStore,
                    new StateSourceOptions<GeneratedWriteRoutingSettings.Fragment>
                    {
                        Writer = leftCompositeStore,
                        Watcher = leftCompositeStore,
                    }
                ),
                new StateSource<GeneratedWriteRoutingSettings.Fragment>(
                    "right",
                    rightCompositeStore,
                    new StateSourceOptions<GeneratedWriteRoutingSettings.Fragment>
                    {
                        Writer = rightCompositeStore,
                        Watcher = rightCompositeStore,
                    }
                ),
            ]),
            writePlan: CreateCompositePlan()
        );
        _compositeContext = BenchmarkContextFactory.Create<
            GeneratedWriteRoutingSettings,
            GeneratedWriteRoutingSettings.Fragment
        >(
            new StateSourceSet<GeneratedWriteRoutingSettings.Fragment>([
                composite.CreateSource("combined"),
            ])
        );
        _compositeState = _compositeContext.GetState<GeneratedWriteRoutingSettings>();
    }

    [GlobalCleanup]
    public async Task CleanupAsync()
    {
        await _routedContext.DisposeAsync().ConfigureAwait(false);
        await _compositeContext.DisposeAsync().ConfigureAwait(false);
    }

    [Benchmark(Description = "Generated typed patch route partition")]
    public int RoutePatch()
    {
        var routed = ((IConfiglueRoutablePatch)_patch).Route(_routePlan, _fallback);
        return routed.Count;
    }

    [Benchmark(Description = "Multi-source routed save with in-memory stores")]
    public async Task SaveRoutedAsync()
    {
        await _routedState.SaveAsync(_patch).ConfigureAwait(false);
    }

    [Benchmark(Description = "Composite routed save with in-memory stores")]
    public async Task SaveCompositeAsync()
    {
        await _compositeState.SaveAsync(_patch).ConfigureAwait(false);
    }

    private static StateWritePlan CreateCompositePlan()
    {
        var routes = new Dictionary<string, SourceId>(StringComparer.Ordinal);
        foreach (var name in LeftMemberNames)
        {
            routes[name] = SourceId.From("left");
        }

        foreach (var name in RightMemberNames)
        {
            routes[name] = SourceId.From("right");
        }

        return new StateWritePlan(null, routes);
    }

    private static GeneratedWriteRoutingSettings.Patch CreatePatch(int presentCount)
    {
        var patch = new GeneratedWriteRoutingSettings.Patch();
        if (presentCount <= 0)
        {
            return patch;
        }

        patch.IntCounter = 7;
        if (presentCount <= 1)
        {
            return patch;
        }

        patch.BoolFlag = true;
        if (presentCount <= 2)
        {
            return patch;
        }

        patch.LongValue = 123456789L;
        if (presentCount <= 3)
        {
            return patch;
        }

        patch.DoubleRatio = 0.5;
        if (presentCount <= 4)
        {
            return patch;
        }

        patch.Level = GeneratedWriteRoutingLevel.Warning;
        if (presentCount <= 5)
        {
            return patch;
        }

        patch.Day = DayOfWeek.Friday;
        if (presentCount <= 6)
        {
            return patch;
        }

        patch.NullableInt = 11;
        if (presentCount <= 7)
        {
            return patch;
        }

        patch.NullableBool = false;
        if (presentCount <= 8)
        {
            return patch;
        }

        patch.NullableLevel = GeneratedWriteRoutingLevel.Error;
        if (presentCount <= 9)
        {
            return patch;
        }

        patch.RequestId = new Guid("11111111-2222-3333-4444-555555555555");
        if (presentCount <= 10)
        {
            return patch;
        }

        patch.NullableRequestId = new Guid("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");
        if (presentCount <= 11)
        {
            return patch;
        }

        patch.Timestamp = new DateTime(2026, 10, 4, 0, 0, 0, DateTimeKind.Utc);
        if (presentCount <= 12)
        {
            return patch;
        }

        patch.NullableTimestamp = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        if (presentCount <= 13)
        {
            return patch;
        }

        patch.Timeout = TimeSpan.FromSeconds(30);
        if (presentCount <= 14)
        {
            return patch;
        }

        patch.NullableTimeout = TimeSpan.FromMinutes(2);
        if (presentCount <= 15)
        {
            return patch;
        }

        patch.Name = "benchmark-routing";
        return patch;
    }

    private static GeneratedWriteRoutingSettings.Fragment CreateLeftSeed() =>
        new()
        {
            IntCounter = Optional<int>.Present(1),
            LongValue = Optional<long>.Present(42L),
            Level = Optional<GeneratedWriteRoutingLevel>.Present(
                GeneratedWriteRoutingLevel.Information
            ),
            NullableInt = Optional<int?>.Present(3),
            NullableLevel = Optional<GeneratedWriteRoutingLevel?>.Present(
                GeneratedWriteRoutingLevel.Debug
            ),
            NullableRequestId = Optional<Guid?>.Present(
                new Guid("55555555-6666-7777-8888-999999999999")
            ),
            NullableTimestamp = Optional<DateTime?>.Present(
                new DateTime(2025, 12, 31, 0, 0, 0, DateTimeKind.Utc)
            ),
            NullableTimeout = Optional<TimeSpan?>.Present(TimeSpan.FromMinutes(1)),
        };

    private static GeneratedWriteRoutingSettings.Fragment CreateRightSeed() =>
        new()
        {
            BoolFlag = Optional<bool>.Present(false),
            DoubleRatio = Optional<double>.Present(0.25),
            Day = Optional<DayOfWeek>.Present(DayOfWeek.Monday),
            NullableBool = Optional<bool?>.Present(true),
            RequestId = Optional<Guid>.Present(new Guid("00000000-1111-2222-3333-444444444444")),
            Timestamp = Optional<DateTime>.Present(
                new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)
            ),
            Timeout = Optional<TimeSpan>.Present(TimeSpan.FromSeconds(10)),
            Name = Optional<string>.Present("seed"),
        };
}
