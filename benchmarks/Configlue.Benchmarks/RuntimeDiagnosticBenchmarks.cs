using BenchmarkDotNet.Attributes;
using Configlue;
using Configlue.Sources;
using Configlue.State;
using Configlue.Testing;

[MemoryDiagnoser]
public class RuntimeDiagnosticBenchmarks
{
    private ConfiglueContext _context = null!;
    private IReadOnlyState<OptimizationBenchmarkSettings> _state = null!;

    [Params("Disabled", "Snapshot", "History")]
    public string Mode { get; set; } = "Disabled";

    [Params(1, 4)]
    public int SourceCount { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        var sources = Enumerable
            .Range(0, SourceCount)
            .Select(index => new StateSource<OptimizationBenchmarkSettings.Fragment>(
                $"source-{index}",
                index == SourceCount - 1
                    ? new InMemoryStateSource<OptimizationBenchmarkSettings.Fragment>(
                        new() { Counter = 10 }
                    )
                    : new InMemoryStateSource<OptimizationBenchmarkSettings.Fragment>(),
                new StateSourceOptions<OptimizationBenchmarkSettings.Fragment>
                {
                    Priority = SourceCount - index,
                    FallbackCondition = StateFallbackCondition.NotFound,
                }
            ))
            .ToArray();
        _context = BenchmarkContextFactory.Create<
            OptimizationBenchmarkSettings,
            OptimizationBenchmarkSettings.Fragment
        >(
            new StateSourceSet<OptimizationBenchmarkSettings.Fragment>(sources),
            model =>
                model.Diagnostics = Mode switch
                {
                    "Disabled" => ConfiglueRuntimeDiagnosticOptions.Disabled,
                    "History" => new ConfiglueRuntimeDiagnosticOptions
                    {
                        EventHistoryCapacity = 64,
                    },
                    _ => ConfiglueRuntimeDiagnosticOptions.Default,
                }
        );
        _state = _context.GetState<OptimizationBenchmarkSettings>();
    }

    [Benchmark]
    public ValueTask<OptimizationBenchmarkSettings> GetValueAsync() => _state.GetValueAsync();

    [GlobalCleanup]
    public ValueTask CleanupAsync() => _context.DisposeAsync();
}
