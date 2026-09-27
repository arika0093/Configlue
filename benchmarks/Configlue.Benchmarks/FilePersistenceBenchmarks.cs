using System.Text.Json;
using BenchmarkDotNet.Attributes;
using Configlue;
using Configlue.Provider.Json;
using Configlue.Testing;
using Configuration.Writable;

[ConfiglueModel("configlue-file-benchmark-settings", Version = 1)]
public partial class PersistenceBenchmarkSettings
{
    public int Counter { get; set; }

    public string Name { get; set; } = string.Empty;

    public bool Enabled { get; set; }
}

[OptionsModel(Id = "writable-file-benchmark-settings", Version = 1)]
public partial class WritablePersistenceBenchmarkSettings
{
    public int Counter { get; set; }

    public string Name { get; set; } = string.Empty;

    public bool Enabled { get; set; }
}

[MemoryDiagnoser]
public class FilePersistenceBenchmarks
{
    private string _directory = null!;
    private FileResource _resource = null!;
    private ConfiglueOptions<
        PersistenceBenchmarkSettings,
        PersistenceBenchmarkSettings.Fragment
    > _configlue = null!;
    private Configuration.Writable.IWritableOptions<WritablePersistenceBenchmarkSettings> _writable =
        null!;
    private int _counter;

    [GlobalSetup]
    public async Task SetupAsync()
    {
        _directory = Path.Combine(Path.GetTempPath(), $"configlue-bench-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_directory);

        var configluePath = Path.Combine(_directory, "configlue.json");
        _resource = new FileResource(configluePath);
        var source = SerializedStateSource.FromResource<PersistenceBenchmarkSettings.Fragment>(
            "benchmark",
            _resource,
            new JsonStateCodec<PersistenceBenchmarkSettings.Fragment>(
                new JsonSerializerOptions { WriteIndented = false }
            ),
            physicalOrigin: configluePath
        );
        _configlue = new ConfiglueOptions<
            PersistenceBenchmarkSettings,
            PersistenceBenchmarkSettings.Fragment
        >(new StateSourceSet<PersistenceBenchmarkSettings.Fragment>([source]));
        _ = await ((Configlue.IReadOnlyOptions<PersistenceBenchmarkSettings>)_configlue)
            .GetValueAsync()
            .ConfigureAwait(false);

        var writablePath = Path.Combine(_directory, "writable.json");
        WritableOptions.Initialize(configuration =>
        {
            configuration.Add<WritablePersistenceBenchmarkSettings>(options =>
                options.UseFile(writablePath)
            );
        });
        _writable = WritableOptions.GetOptions<WritablePersistenceBenchmarkSettings>();
        _ = _writable.CurrentValue;
    }

    [GlobalCleanup]
    public async Task CleanupAsync()
    {
        await _configlue.DisposeAsync().ConfigureAwait(false);
        if (_writable is IAsyncDisposable asyncDisposable)
        {
            await asyncDisposable.DisposeAsync().ConfigureAwait(false);
        }
        else if (_writable is IDisposable disposable)
        {
            disposable.Dispose();
        }
        _resource.Dispose();
        Directory.Delete(_directory, recursive: true);
    }

    [Benchmark]
    public PersistenceBenchmarkSettings ConfiglueCurrentValue() => _configlue.CurrentValue;

    [Benchmark]
    public WritablePersistenceBenchmarkSettings ConfigurationWritableCurrentValue() =>
        _writable.CurrentValue;

    [Benchmark]
    public async Task ConfiglueSaveAsync()
    {
        var next = Interlocked.Increment(ref _counter);
        await _configlue.SaveAsync(patch => patch.Counter = next).ConfigureAwait(false);
    }

    [Benchmark]
    public async Task ConfigurationWritableSaveAsync()
    {
        var next = Interlocked.Increment(ref _counter);
        await _writable.SaveAsync(settings => settings.Counter = next).ConfigureAwait(false);
    }
}

[MemoryDiagnoser]
public class LayeredResolutionBenchmarks
{
    private ConfiglueOptions<BenchmarkSettings, BenchmarkSettings.Fragment> _options = null!;

    [Params(1, 4, 16)]
    public int SourceCount { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        var sources = Enumerable
            .Range(0, SourceCount)
            .Select(index =>
            {
                var store = new InMemoryStateStore<BenchmarkSettings.Fragment>(
                    new BenchmarkSettings.Fragment
                    {
                        Counter = Optional<int>.Present(index),
                        Name = Optional<string>.Present($"Layer {index}"),
                        Enabled = Optional<bool>.Present(index % 2 == 0),
                    }
                );
                return new StateSource<BenchmarkSettings.Fragment>(
                    $"layer-{index}",
                    store,
                    priority: SourceCount - index
                );
            })
            .ToArray();
        _options = new ConfiglueOptions<BenchmarkSettings, BenchmarkSettings.Fragment>(
            new StateSourceSet<BenchmarkSettings.Fragment>(sources)
        );
    }

    [GlobalCleanup]
    public ValueTask CleanupAsync() => _options.DisposeAsync();

    [Benchmark]
    public ValueTask<BenchmarkSettings> ResolveSourcesAsync() =>
        ((Configlue.IReadOnlyOptions<BenchmarkSettings>)_options).GetValueAsync();
}
