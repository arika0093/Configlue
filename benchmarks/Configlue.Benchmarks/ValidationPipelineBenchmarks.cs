using BenchmarkDotNet.Attributes;
using Configlue;
using Configlue.Testing;

[ConfiglueModel("bench-unvalidated-settings")]
public partial class UnvalidatedBenchmarkSettings
{
    public int Counter { get; set; }
    public string Name { get; set; } = "default";
}

[MemoryDiagnoser]
public class ValidationPipelineBenchmarks
{
    private RuntimeValidationPipeline<
        UnvalidatedBenchmarkSettings,
        UnvalidatedBenchmarkSettings.Fragment
    > _pipeline = null!;
    private UnvalidatedBenchmarkSettings _model = null!;
    private UnvalidatedBenchmarkSettings.Fragment _fragment = null!;

    [Params(false, true)]
    public bool DataAnnotations { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        var diagnostics = new RuntimeDiagnosticRecorder(
            "bench",
            "bench-unvalidated-settings",
            1,
            ConfiglueRuntimeDiagnosticOptions.Disabled,
            []
        );
        _pipeline = new([], DataAnnotations, "bench", diagnostics);
        _model = new() { Counter = 10, Name = "benchmark" };
        _fragment = RuntimeModel<
            UnvalidatedBenchmarkSettings,
            UnvalidatedBenchmarkSettings.Fragment
        >.ToFragment(_model);
        _pipeline.ValidateResolvedModel(_model, _fragment);
    }

    [Benchmark]
    public void EffectiveModel() => _pipeline.ValidateResolvedModel(_model, _fragment);
}
