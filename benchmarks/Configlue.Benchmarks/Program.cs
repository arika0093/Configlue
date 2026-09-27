using System.Threading.Tasks.Sources;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Running;
using Configlue;
using Configlue.Testing;

BenchmarkSwitcher.FromAssembly(typeof(OptionsRuntimeBenchmarks).Assembly).Run(args);

[ConfiglueModel("configlue-benchmark-settings", Version = 1)]
public partial class BenchmarkSettings
{
    public int Counter { get; set; }

    public string Name { get; set; } = string.Empty;

    public bool Enabled { get; set; }
}

[MemoryDiagnoser]
public class OptionsRuntimeBenchmarks
{
    private InMemoryStateStore<BenchmarkSettings.Fragment> _store = null!;
    private ConfiglueOptions<BenchmarkSettings, BenchmarkSettings.Fragment> _options = null!;
    private IReadOnlyOptions<BenchmarkSettings> _readOptions = null!;
    private IDisposable _subscription = null!;
    private readonly AsyncPulse _publishCompleted = new();
    private int _counter;

    [GlobalSetup]
    public async Task SetupAsync()
    {
        _store = new InMemoryStateStore<BenchmarkSettings.Fragment>(CreateFragment(0));
        _options = new ConfiglueOptions<BenchmarkSettings, BenchmarkSettings.Fragment>(
            new StateSourceSet<BenchmarkSettings.Fragment>([
                new StateSource<BenchmarkSettings.Fragment>(
                    "benchmark",
                    _store,
                    writer: _store,
                    watcher: _store
                ),
            ]),
            onChangeDebounce: TimeSpan.Zero
        );
        _readOptions = _options;
        _ = await _readOptions.GetValueAsync().ConfigureAwait(false);
        _subscription = _options.OnChange(_ => _publishCompleted.Set());
    }

    [GlobalCleanup]
    public async Task CleanupAsync()
    {
        _subscription.Dispose();
        await _options.DisposeAsync().ConfigureAwait(false);
    }

    [Benchmark]
    public ValueTask<BenchmarkSettings> GetValueAsync() => _readOptions.GetValueAsync();

    [Benchmark]
    public async Task PublishChangeAsync()
    {
        var completed = _publishCompleted.WaitAsync();
        var next = Interlocked.Increment(ref _counter);
        _store.Set(CreateFragment(next));
        await completed.ConfigureAwait(false);
    }

    private static BenchmarkSettings.Fragment CreateFragment(int counter) =>
        new()
        {
            Counter = Optional<int>.Present(counter),
            Name = Optional<string>.Present("Configlue"),
            Enabled = Optional<bool>.Present(true),
        };

    private sealed class AsyncPulse : IValueTaskSource
    {
        private ManualResetValueTaskSourceCore<bool> _core = new()
        {
            RunContinuationsAsynchronously = true,
        };

        public ValueTask WaitAsync()
        {
            _core.Reset();
            return new ValueTask(this, _core.Version);
        }

        public void Set() => _core.SetResult(true);

        public void GetResult(short token) => _ = _core.GetResult(token);

        public ValueTaskSourceStatus GetStatus(short token) => _core.GetStatus(token);

        public void OnCompleted(
            Action<object?> continuation,
            object? state,
            short token,
            ValueTaskSourceOnCompletedFlags flags
        ) => _core.OnCompleted(continuation, state, token, flags);
    }
}
