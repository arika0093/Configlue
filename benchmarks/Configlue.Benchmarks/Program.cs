using System.Threading.Tasks.Sources;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Running;
using Configlue;
using Configlue.Extensions.MSOptions;
using Configlue.Sources;
using Configlue.State;
using Configlue.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

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
    private ConfiglueRuntime<BenchmarkSettings, BenchmarkSettings.Fragment> _options = null!;
    private IReadOnlyState<BenchmarkSettings> _readOptions = null!;
    private IOptionsMonitor<BenchmarkSettings> _monitor = null!;
    private ServiceProvider _serviceProvider = null!;
    private IDisposable _subscription = null!;
    private readonly AsyncPulse _publishCompleted = new();
    private int _counter;

    [GlobalSetup]
    public async Task SetupAsync()
    {
        _store = new InMemoryStateStore<BenchmarkSettings.Fragment>(CreateFragment(0));
        var sourceSet = new StateSourceSet<BenchmarkSettings.Fragment>([
            new StateSource<BenchmarkSettings.Fragment>(
                "benchmark",
                _store,
                writer: _store,
                watcher: _store
            ),
        ]);
        var services = new ServiceCollection();
        services.AddConfiglueState<BenchmarkSettings, BenchmarkSettings.Fragment>(
            sourceSet,
            onChangeDebounce: TimeSpan.Zero
        );
        services.AddConfiglueMicrosoftOptions<BenchmarkSettings>();
        _serviceProvider = services.BuildServiceProvider();
        _options = _serviceProvider.GetRequiredService<
            ConfiglueRuntime<BenchmarkSettings, BenchmarkSettings.Fragment>
        >();
        _readOptions = _options;
        _monitor = _serviceProvider.GetRequiredService<IOptionsMonitor<BenchmarkSettings>>();
        _ = await _readOptions.GetValueAsync().ConfigureAwait(false);
        _ = await _readOptions.GetValueAsync().ConfigureAwait(false);
        _ = _monitor.CurrentValue;
        _subscription = _options.OnChange(_ => _publishCompleted.Set());
    }

    [GlobalCleanup]
    public async Task CleanupAsync()
    {
        _subscription.Dispose();
        await _serviceProvider.DisposeAsync().ConfigureAwait(false);
    }

    [Benchmark]
    public ValueTask<BenchmarkSettings> GetValueAsync() => _readOptions.GetValueAsync();

    [Benchmark]
    public BenchmarkSettings MonitorCurrentValue() => _monitor.CurrentValue;

    [Benchmark]
    public ValueTask<BenchmarkSettings> FacadeGetValueAsync() =>
        ((IReadOnlyState<BenchmarkSettings>)_options).GetValueAsync();

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

[MemoryDiagnoser]
public class StateSourceResolverBenchmarks
{
    private StateSourceResolver<BenchmarkSettings.Fragment> _resolver = null!;

    [Params(1, 4, 16)]
    public int SourceCount { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        var sources = Enumerable
            .Range(0, SourceCount)
            .Select(index =>
            {
                var store =
                    index == SourceCount - 1
                        ? new InMemoryStateStore<BenchmarkSettings.Fragment>(
                            new BenchmarkSettings.Fragment
                            {
                                Counter = Optional<int>.Present(index),
                                Name = Optional<string>.Present($"Layer {index}"),
                                Enabled = Optional<bool>.Present(true),
                            }
                        )
                        : new InMemoryStateStore<BenchmarkSettings.Fragment>();
                return new StateSource<BenchmarkSettings.Fragment>(
                    $"layer-{index}",
                    store,
                    priority: SourceCount - index,
                    fallbackCondition: StateFallbackCondition.NotFound
                );
            })
            .ToArray();
        _resolver = new StateSourceResolver<BenchmarkSettings.Fragment>(
            new StateSourceSet<BenchmarkSettings.Fragment>(sources)
        );
    }

    [Benchmark]
    public ValueTask<StateReadResult<BenchmarkSettings.Fragment>> ResolveSourcesAsync() =>
        _resolver.ReadAsync();
}
