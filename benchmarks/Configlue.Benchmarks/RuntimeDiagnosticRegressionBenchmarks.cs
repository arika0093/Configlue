using System.Diagnostics.Metrics;
using BenchmarkDotNet.Attributes;
using Configlue;
using Configlue.Sources;
using Configlue.State;
using Configlue.Testing;

/// <summary>
/// Explicit regression comparison for issue #223: disabled fast path versus
/// snapshot, history, explicit-listener, and telemetry-observed reads.
/// <see cref="RuntimeDiagnosticBenchmarks"/> is left untouched; this class adds the
/// listener/telemetry modes and treats <c>Disabled</c> as the internal baseline.
/// The pre-diagnostics reference (1 source ~756 ns, 4 sources ~991 ns, see
/// <c>benchmarks/runtime-diagnostics-results.md</c>) is a historic record from before
/// runtime diagnostics existed and cannot be reproduced on this commit without
/// checking out the old sources, so <c>Disabled</c> doubles as the in-tree baseline:
/// after the fast-path change it should sit at or near that historic floor with no
/// additional allocation. Activity-listener tracing shares the same enablement gate
/// (<c>ConfiglueTelemetry.IsEnabled</c> via <c>ActivitySource.HasListeners</c>) and is
/// covered by <c>RuntimeDiagnosticTests.Tracing_*</c>; the <c>Telemetry</c> mode below
/// exercises the metrics side with a <see cref="MeterListener"/>.
/// </summary>
[MemoryDiagnoser]
public class RuntimeDiagnosticRegressionBenchmarks
{
    private ConfiglueContext _context = null!;
    private IReadOnlyState<OptimizationBenchmarkSettings> _state = null!;
    private IDisposable? _diagnosticSubscription;
    private MeterListener? _meterListener;

    [Params("Disabled", "Snapshot", "History", "Listener", "Telemetry")]
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
        // Listener and Telemetry modes build on fully-disabled options so the measured
        // cost is exactly the dynamic-observer path; snapshot/history stay off.
        _context = BenchmarkContextFactory.Create<
            OptimizationBenchmarkSettings,
            OptimizationBenchmarkSettings.Fragment
        >(
            new StateSourceSet<OptimizationBenchmarkSettings.Fragment>(sources),
            model =>
                model.Diagnostics = Mode switch
                {
                    "Snapshot" => ConfiglueRuntimeDiagnosticOptions.Default,
                    "History" => new ConfiglueRuntimeDiagnosticOptions
                    {
                        EventHistoryCapacity = 64,
                    },
                    _ => ConfiglueRuntimeDiagnosticOptions.Disabled,
                }
        );
        _state = _context.GetState<OptimizationBenchmarkSettings>();

        if (Mode == "Listener")
        {
            _diagnosticSubscription = _context
                .GetDiagnostics<OptimizationBenchmarkSettings>()
                .OnDiagnosticEvent(static _ => { });
        }

        if (Mode == "Telemetry")
        {
            var listener = new MeterListener
            {
                InstrumentPublished = static (instrument, owner) =>
                {
                    if (instrument.Meter.Name == ConfiglueTelemetry.MeterName)
                        owner.EnableMeasurementEvents(instrument);
                },
            };
            listener.SetMeasurementEventCallback<double>(static (_, _, _, _) => { });
            listener.SetMeasurementEventCallback<long>(static (_, _, _, _) => { });
            listener.Start();
            _meterListener = listener;
        }
    }

    [Benchmark]
    public ValueTask<OptimizationBenchmarkSettings> GetValueAsync() => _state.GetValueAsync();

    [GlobalCleanup]
    public async ValueTask CleanupAsync()
    {
        _diagnosticSubscription?.Dispose();
        _diagnosticSubscription = null;
        _meterListener?.Dispose();
        _meterListener = null;
        await _context.DisposeAsync();
    }
}
