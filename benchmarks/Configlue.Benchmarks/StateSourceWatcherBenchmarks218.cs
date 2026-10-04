using BenchmarkDotNet.Attributes;
using Configlue.Resources;
using Configlue.Sources;
using Configlue.State;

// Issue #218: 0/1/N fast paths for StateSourceWatcher<T>.
// Measures allocation + completion overhead without external I/O using
// synchronous fake watchers. The one-watcher benchmark completes via the
// dedicated single-target path (no linked CTS, no task list, no
// WhenAny/WhenAll), so restoring the generic
// List<Task> + linked CTS + WhenAny/WhenAll path regresses its allocated
// bytes.
[MemoryDiagnoser]
public class StateSourceWatcherBenchmarks218
{
    private StateSourceWatcher<object> _zeroWatcher = null!;
    private StateSourceWatcher<object> _oneWatcher = null!;
    private StateSourceWatcher<object> _twoWatchers = null!;
    private StateSourceWatcher<object> _severalWatchers = null!;
    private CancellationToken _canceledToken = new(canceled: true);

    [GlobalSetup]
    public void Setup()
    {
        _zeroWatcher = CreateWatcher(watchableSources: 0, totalSources: 2);
        _oneWatcher = CreateWatcher(watchableSources: 1, totalSources: 1);
        _twoWatchers = CreateWatcher(watchableSources: 2, totalSources: 2);
        _severalWatchers = CreateWatcher(watchableSources: 5, totalSources: 5);

        // Warm up each path (including the zero-path cancellation throw).
        try
        {
            _zeroWatcher.WaitForChangeAsync(ConfiglueResourceContext.Default, null, _canceledToken).GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
        }

        _oneWatcher.WaitForChangeAsync(ConfiglueResourceContext.Default, null).GetAwaiter().GetResult();
        _twoWatchers.WaitForChangeAsync(ConfiglueResourceContext.Default, null).GetAwaiter().GetResult();
        _severalWatchers.WaitForChangeAsync(ConfiglueResourceContext.Default, null).GetAwaiter().GetResult();
    }

    [Benchmark]
    public bool ZeroWatcherCancelled()
    {
        try
        {
            _zeroWatcher.WaitForChangeAsync(ConfiglueResourceContext.Default, null, _canceledToken).GetAwaiter().GetResult();
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    [Benchmark(Baseline = true)]
    public void OneWatcher() =>
        _oneWatcher.WaitForChangeAsync(ConfiglueResourceContext.Default, null).GetAwaiter().GetResult();

    [Benchmark]
    public void TwoWatchers() =>
        _twoWatchers.WaitForChangeAsync(ConfiglueResourceContext.Default, null).GetAwaiter().GetResult();

    [Benchmark]
    public void SeveralWatchers() =>
        _severalWatchers.WaitForChangeAsync(ConfiglueResourceContext.Default, null).GetAwaiter().GetResult();

    private static StateSourceWatcher<object> CreateWatcher(int watchableSources, int totalSources)
    {
        var reader = new FakeReader();
        var completedWatcher = new CompletedWatcher();
        var sources = new StateSource<object>[totalSources];
        for (var index = 0; index < totalSources; index++)
        {
            var watcher = index < watchableSources ? completedWatcher : null;
            sources[index] = new StateSource<object>(
                $"bench-218-watcher-{totalSources}-{index}",
                reader,
                new StateSourceOptions<object> { Watcher = watcher });
        }

        return new StateSourceWatcher<object>(
            new StateSourceResolver<object>(new StateSourceSet<object>(sources)));
    }

    private sealed class FakeReader : ISourceReader<object>
    {
        public ValueTask<StateReadResult<object>> ReadAsync(
            ConfiglueResourceContext context,
            CancellationToken cancellationToken = default)
        {
            _ = context;
            _ = cancellationToken;
            return new ValueTask<StateReadResult<object>>(StateReadResult<object>.NotFound("bench-218"));
        }
    }

    private sealed class CompletedWatcher : ISourceWatcher
    {
        public ValueTask WaitForChangeAsync(
            ConfiglueResourceContext context,
            string? observedRevision,
            CancellationToken cancellationToken = default)
        {
            _ = context;
            _ = observedRevision;
            _ = cancellationToken;
            return default;
        }
    }
}
