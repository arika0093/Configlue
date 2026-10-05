using BenchmarkDotNet.Attributes;
using Configlue;
using Configlue.Resources;
using Configlue.Sources;
using Configlue.State;

/// <summary>Measures collection clone costs through a public runtime read.</summary>
[MemoryDiagnoser]
public class CollectionModelReadBenchmarks
{
    [Params(0, 16, 4096)]
    public int Count { get; set; }

    private ConfiglueContext _context = null!;
    private IReadOnlyState<FragmentEqualityCollectionSettings> _state = null!;
    private FragmentEqualityCollectionSettings _model = null!;

    [GlobalSetup]
    public async Task SetupAsync()
    {
        var numbers = Enumerable.Range(0, Count).ToList();
        _model = new()
        {
            Numbers = numbers,
            Scores = numbers.ToArray(),
            Lookup = numbers.ToDictionary(static number => $"key-{number}"),
            Tags = numbers.Select(static number => $"tag-{number}").ToHashSet(),
        };
        var reader = new Reader(
            StateReadResult<FragmentEqualityCollectionSettings.Fragment>.Success(
                FragmentEqualityCollectionSettings.Fragment.From(_model),
                "stable"
            )
        );
        _context = BenchmarkContextFactory.Create<
            FragmentEqualityCollectionSettings,
            FragmentEqualityCollectionSettings.Fragment
        >(
            new([
                new StateSource<FragmentEqualityCollectionSettings.Fragment>(
                    "collections",
                    reader,
                    new()
                ),
            ]),
            model => model.Diagnostics = ConfiglueRuntimeDiagnosticOptions.Disabled
        );
        _state = _context.GetState<FragmentEqualityCollectionSettings>();
        var first = await _state.GetValueAsync();
        first.Numbers.Clear();
        first.Lookup.Clear();
        first.Tags.Clear();
        var second = await _state.GetValueAsync();
        if (
            second.Numbers.Count != Count
            || second.Lookup.Count != Count
            || second.Tags.Count != Count
            || second.Scores.Length != Count
        )
        {
            throw new InvalidOperationException("Runtime reads did not isolate collection values.");
        }
    }

    [Benchmark(Baseline = true)]
    public FragmentEqualityCollectionSettings DirectModelClone() => _model.DeepClone();

    [Benchmark]
    public ValueTask<FragmentEqualityCollectionSettings> GetValueAsync() => _state.GetValueAsync();

    [GlobalCleanup]
    public ValueTask CleanupAsync() => _context.DisposeAsync();

    private sealed class Reader(StateReadResult<FragmentEqualityCollectionSettings.Fragment> result)
        : ISourceReader<FragmentEqualityCollectionSettings.Fragment>
    {
        public ValueTask<StateReadResult<FragmentEqualityCollectionSettings.Fragment>> ReadAsync(
            ConfiglueResourceContext context,
            CancellationToken cancellationToken = default
        ) => new(result);
    }
}
