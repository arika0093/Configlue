using BenchmarkDotNet.Attributes;
using Configlue;
using Configlue.Resources;
using Configlue.Sources;
using Configlue.State;

[MemoryDiagnoser]
public class RuntimeRevisionSnapshotBenchmarks
{
    private ConfiglueContext _context = null!;
    private IReadOnlyState<BenchmarkSettings> _state = null!;

    [Params(1, 4, 16)]
    public int SourceCount { get; set; }

    [Params("Stable", "Revision", "Nested")]
    public string Change { get; set; } = "Stable";

    [GlobalSetup]
    public void Setup()
    {
        var sources = Enumerable
            .Range(0, SourceCount)
            .Select(index => new StateSource<BenchmarkSettings.Fragment>(
                $"source-{index}",
                index == SourceCount - 1
                    ? CreateValueReader()
                    : new Reader(
                        StateReadResult<BenchmarkSettings.Fragment>.NotFound("missing"),
                        StateReadResult<BenchmarkSettings.Fragment>.NotFound("missing"),
                        false
                    ),
                new StateSourceOptions<BenchmarkSettings.Fragment>
                {
                    Priority = SourceCount - index,
                    FallbackCondition = StateFallbackCondition.NotFound,
                }
            ))
            .ToArray();
        _context = BenchmarkContextFactory.Create<BenchmarkSettings, BenchmarkSettings.Fragment>(
            new(sources),
            model => model.Diagnostics = ConfiglueRuntimeDiagnosticOptions.Disabled
        );
        _state = _context.GetState<BenchmarkSettings>();
        _ = _state.GetValueAsync().GetAwaiter().GetResult();
    }

    private Reader CreateValueReader()
    {
        var value = new BenchmarkSettings.Fragment
        {
            Counter = 42,
            Name = "value",
            Enabled = true,
        };
        var first = StateReadResult<BenchmarkSettings.Fragment>.Success(value, "a");
        var second = StateReadResult<BenchmarkSettings.Fragment>.Success(value, "b");
        if (Change == "Nested")
        {
            first = first with
            {
                Revisions = StateRevisionVector.FromSingle(new(SourceId.From("child"), "a")),
            };
            second = first with
            {
                Revisions = StateRevisionVector.FromSingle(new(SourceId.From("child"), "b")),
            };
        }
        return new(first, second, Change != "Stable");
    }

    [Benchmark]
    public ValueTask<BenchmarkSettings> GetValueAsync() => _state.GetValueAsync();

    [GlobalCleanup]
    public ValueTask CleanupAsync() => _context.DisposeAsync();

    private sealed class Reader(
        StateReadResult<BenchmarkSettings.Fragment> first,
        StateReadResult<BenchmarkSettings.Fragment> second,
        bool changing
    ) : ISourceReader<BenchmarkSettings.Fragment>
    {
        private bool _second;

        public ValueTask<StateReadResult<BenchmarkSettings.Fragment>> ReadAsync(
            ConfiglueResourceContext context,
            CancellationToken cancellationToken = default
        )
        {
            if (changing)
                _second = !_second;
            return new(_second ? second : first);
        }
    }
}
