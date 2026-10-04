using System.Text.Json;
using BenchmarkDotNet.Attributes;
using Configlue;
using Configlue.Extensibility;
using Configlue.Provider.Json;
using Configlue.Resources;
using Configlue.Sources;
using Configlue.State;
using Configlue.Testing;
using Configuration.Writable;

[ConfiglueModel("configlue-file-benchmark-settings", Version = 1)]
public partial class PersistenceBenchmarkSettings
{
    public int Counter { get; set; }

    public string Name { get; set; } = string.Empty;

    public bool Enabled { get; set; }
}

[ConfiglueModel("configlue-json-read-benchmark-settings", Version = 1)]
public partial class SerializedReadBenchmarkSettings
{
    public string Payload { get; set; } = string.Empty;
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
    private FileResource _resourceWithoutBackup = null!;
    private ConfiglueContext _configlueContext = null!;
    private ConfiglueContext _configlueWithoutBackupContext = null!;
    private ConfiglueContext _configlueInMemoryContext = null!;
    private IWritableState<PersistenceBenchmarkSettings> _configlue = null!;
    private IWritableState<PersistenceBenchmarkSettings> _configlueWithoutBackup = null!;
    private IWritableState<PersistenceBenchmarkSettings> _configlueInMemory = null!;
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
        _configlueContext = CreateConfiglueContext(configluePath, _resource);
        _configlue = _configlueContext.GetState<PersistenceBenchmarkSettings>();
        _ = await _configlue.GetValueAsync().ConfigureAwait(false);

        var configluePathWithoutBackup = Path.Combine(_directory, "configlue-no-backup.json");
        _resourceWithoutBackup = new FileResource(
            configluePathWithoutBackup,
            new FileResourceOptions { CreateBackup = false }
        );
        _configlueWithoutBackupContext = CreateConfiglueContext(
            configluePathWithoutBackup,
            _resourceWithoutBackup
        );
        _configlueWithoutBackup =
            _configlueWithoutBackupContext.GetState<PersistenceBenchmarkSettings>();
        _ = await _configlueWithoutBackup.GetValueAsync().ConfigureAwait(false);

        var inMemoryStore = new InMemoryStateSource<PersistenceBenchmarkSettings.Fragment>(
            new PersistenceBenchmarkSettings.Fragment
            {
                Counter = Optional<int>.Present(0),
                Name = Optional<string>.Present("Benchmark"),
                Enabled = Optional<bool>.Present(true),
            }
        );
        _configlueInMemoryContext = BenchmarkContextFactory.Create<
            PersistenceBenchmarkSettings,
            PersistenceBenchmarkSettings.Fragment
        >(
            new StateSourceSet<PersistenceBenchmarkSettings.Fragment>([
                new StateSource<PersistenceBenchmarkSettings.Fragment>(
                    "benchmark",
                    inMemoryStore,
                    new StateSourceOptions<PersistenceBenchmarkSettings.Fragment>
                    {
                        Writer = inMemoryStore,
                    }
                ),
            ])
        );
        _configlueInMemory = _configlueInMemoryContext.GetState<PersistenceBenchmarkSettings>();

        var writablePath = Path.Combine(_directory, "writable.json");
        WritableOptions.Initialize(configuration =>
        {
            configuration.Add<WritablePersistenceBenchmarkSettings>(options =>
                options.UseFile(writablePath)
            );
        });
        _writable = WritableOptions.GetOptions<WritablePersistenceBenchmarkSettings>();
        _ = await _configlue.GetValueAsync().ConfigureAwait(false);
    }

    [GlobalCleanup]
    public async Task CleanupAsync()
    {
        await _configlueContext.DisposeAsync().ConfigureAwait(false);
        await _configlueWithoutBackupContext.DisposeAsync().ConfigureAwait(false);
        await _configlueInMemoryContext.DisposeAsync().ConfigureAwait(false);
        if (_writable is IAsyncDisposable asyncDisposable)
        {
            await asyncDisposable.DisposeAsync().ConfigureAwait(false);
        }
        else if (_writable is IDisposable disposable)
        {
            disposable.Dispose();
        }
        _resource.Dispose();
        _resourceWithoutBackup.Dispose();
        Directory.Delete(_directory, recursive: true);
    }

    [Benchmark]
    public ValueTask<PersistenceBenchmarkSettings> ConfiglueGetValueAsync() =>
        _configlue.GetValueAsync();

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
    public async Task ConfiglueSaveWithoutBackupAsync()
    {
        var next = Interlocked.Increment(ref _counter);
        await _configlueWithoutBackup
            .SaveAsync(patch => patch.Counter = next)
            .ConfigureAwait(false);
    }

    [Benchmark]
    public async Task ConfiglueSaveInMemoryAsync()
    {
        var next = Interlocked.Increment(ref _counter);
        await _configlueInMemory.SaveAsync(patch => patch.Counter = next).ConfigureAwait(false);
    }

    [Benchmark]
    public async Task ConfigurationWritableSaveAsync()
    {
        var next = Interlocked.Increment(ref _counter);
        await _writable.SaveAsync(settings => settings.Counter = next).ConfigureAwait(false);
    }

    private static ConfiglueContext CreateConfiglueContext(string path, FileResource resource)
    {
        var source = new StateSource<PersistenceBenchmarkSettings.Fragment>("benchmark", new SerializedSource<PersistenceBenchmarkSettings.Fragment>(resource, new JsonStateCodec<PersistenceBenchmarkSettings.Fragment>(
                new JsonSerializerOptions { WriteIndented = false }
            ), writer: (IResourceReader)resource as IResourceWriter, watcher: (IResourceReader)resource as ISourceWatcher), new StateSourceOptions<PersistenceBenchmarkSettings.Fragment> { PhysicalOrigin = path });
        return BenchmarkContextFactory.Create<
            PersistenceBenchmarkSettings,
            PersistenceBenchmarkSettings.Fragment
        >(new StateSourceSet<PersistenceBenchmarkSettings.Fragment>([source]));
    }
}

[MemoryDiagnoser]
public class LayeredResolutionBenchmarks
{
    private ConfiglueContext _context = null!;
    private IWritableState<BenchmarkSettings> _options = null!;

    [Params(1, 4, 16)]
    public int SourceCount { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        var sources = Enumerable
            .Range(0, SourceCount)
            .Select(index =>
            {
                var store = new InMemoryStateSource<BenchmarkSettings.Fragment>(
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
                    new StateSourceOptions<BenchmarkSettings.Fragment>
                    {
                        Priority = SourceCount - index,
                    }
                );
            })
            .ToArray();
        _context = BenchmarkContextFactory.Create<BenchmarkSettings, BenchmarkSettings.Fragment>(
            new StateSourceSet<BenchmarkSettings.Fragment>(sources)
        );
        _options = _context.GetState<BenchmarkSettings>();
    }

    [GlobalCleanup]
    public ValueTask CleanupAsync() => _context.DisposeAsync();

    [Benchmark]
    public ValueTask<BenchmarkSettings> ResolveSourcesAsync() =>
        ((Configlue.IReadOnlyState<BenchmarkSettings>)_options).GetValueAsync();
}

[MemoryDiagnoser]
public class FileResourceReadBenchmarks
{
    private string _directory = null!;
    private FileResource _resource = null!;

    [Params(1024, 65536, 1048576)]
    public int ContentSize { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _directory = Path.Combine(Path.GetTempPath(), $"configlue-read-bench-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_directory);

        var path = Path.Combine(_directory, "content.bin");
        File.WriteAllBytes(path, new byte[ContentSize]);
        _resource = new FileResource(
            path,
            new FileResourceOptions { CreateBackup = false }
        );
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _resource.Dispose();
        Directory.Delete(_directory, recursive: true);
    }

    [Benchmark]
    public ValueTask<ResourceReadResult> ReadAsync() =>
        _resource.ReadAsync(ConfiglueResourceContext.Default);
}

[MemoryDiagnoser]
public class SerializedFileReadBenchmarks
{
    private string _directory = null!;
    private SerializedStateReader<SerializedReadBenchmarkSettings.Fragment> _reader = null!;
    private SerializedStateReader<SerializedReadBenchmarkSettings.Fragment> _memoryReader = null!;
    private FileResource _resource = null!;

    [Params(1024, 65536, 1048576, 4194304)]
    public int ContentSize { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _directory = Path.Combine(
            Path.GetTempPath(),
            $"configlue-json-read-bench-{Guid.NewGuid():N}"
        );
        Directory.CreateDirectory(_directory);

        var path = Path.Combine(_directory, "content.json");
        File.WriteAllText(
            path,
            $$"""{"$version":1,"Payload":"{{new string('x', ContentSize)}}"}"""
        );
        _resource = new FileResource(
            path,
            new FileResourceOptions { CreateBackup = false }
        );
        _reader = new SerializedStateReader<SerializedReadBenchmarkSettings.Fragment>(
            _resource,
            new JsonStateCodec<SerializedReadBenchmarkSettings.Fragment>
            {
                UseAsyncStreamDecoding = true,
            }
        );
        _memoryReader = new SerializedStateReader<SerializedReadBenchmarkSettings.Fragment>(
            new MemoryOnlyResourceReader(_resource),
            new JsonStateCodec<SerializedReadBenchmarkSettings.Fragment>()
        );
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _resource.Dispose();
        Directory.Delete(_directory, recursive: true);
    }

    [Benchmark]
    public ValueTask<StateReadResult<SerializedReadBenchmarkSettings.Fragment>> ReadAsync() =>
        _reader.ReadAsync(ConfiglueResourceContext.Default);

    [Benchmark]
    public ValueTask<
        StateReadResult<SerializedReadBenchmarkSettings.Fragment>
    > ReadMemoryFallbackAsync() => _memoryReader.ReadAsync(ConfiglueResourceContext.Default);

    private sealed class MemoryOnlyResourceReader(IResourceReader inner) : IResourceReader
    {
        public ValueTask<ResourceReadResult> ReadAsync(
            ConfiglueResourceContext context,
            CancellationToken cancellationToken = default
        ) => inner.ReadAsync(context, cancellationToken);
    }
}

[MemoryDiagnoser]
public class FileResourceWriteBenchmarks
{
    private string _directory = null!;
    private FileResource _resource = null!;
    private byte[] _content = null!;

    [Params(1024, 65536, 1048576)]
    public int ContentSize { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _directory = Path.Combine(Path.GetTempPath(), $"configlue-write-bench-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_directory);

        var path = Path.Combine(_directory, "content.bin");
        _content = new byte[ContentSize];
        File.WriteAllBytes(path, _content);
        _resource = new FileResource(
            path,
            new FileResourceOptions
            {
                CreateBackup = false,
            }
        );
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _resource.Dispose();
        Directory.Delete(_directory, recursive: true);
    }

    [Benchmark]
    public ValueTask<StateWriteResult> WriteAsync() =>
        _resource.WriteAsync(
            ConfiglueResourceContext.Default,
            new ResourceWriteRequest(_content)
        );
}
