using BenchmarkDotNet.Attributes;
using Configlue;
using Configlue.CompilerServices;

[ConfiglueModel("route-benchmark-root", Version = 1)]
public partial class RouteBenchmarkRoot
{
    public RouteBenchmarkNested Left { get; set; } = new();
    public RouteBenchmarkNested Right { get; set; } = new();
}

[ConfiglueModel("route-benchmark-nested", Version = 1)]
public partial class RouteBenchmarkNested
{
    public string Host { get; set; } = "localhost";
    public int Port { get; set; } = 5432;
}

[MemoryDiagnoser]
public class WriteRouteBenchmarks
{
    private StateWritePlan _compiled = null!;
    private StateWritePlan _diagnostic = null!;
    private ConfiglueMemberPath _path;

    [GlobalSetup]
    public void Setup()
    {
        _compiled = StateWritePlan
            .For<RouteBenchmarkRoot>()
            .Route(x => x.Left, SourceKey<RouteBenchmarkRoot>.Named("left"))
            .Route(x => x.Left.Host, SourceKey<RouteBenchmarkRoot>.Named("host"))
            .Route(x => x.Right, SourceKey<RouteBenchmarkRoot>.Named("right"))
            .Build();
        _diagnostic = new StateWritePlan(
            _compiled.PropertyRoutes.ToDictionary(
                static route => route.Key,
                static route => route.Value.Value
            )
        );
        _path = ConfiglueMemberPath.FromNames(RouteBenchmarkRoot.ConfiglueSchema, "Left.Host");
    }

    [Benchmark(Baseline = true)]
    public SourceId? DiagnosticStringLookup() => _diagnostic.ResolveSourceIdOrNull("Left.Host");

    [Benchmark]
    public SourceId? GeneratedIdentityLookup() => ConfiglueWriteRouting.Resolve(_compiled, _path);

    [Benchmark]
    public bool GeneratedRouteBelow() =>
        ConfiglueWriteRouting.HasRouteBelow(
            _compiled,
            ConfiglueMemberPath.Root(RouteBenchmarkRoot.ConfiglueSchema)
        );
}
