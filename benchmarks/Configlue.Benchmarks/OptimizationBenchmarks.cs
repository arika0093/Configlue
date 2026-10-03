using System.Buffers;
using System.CommandLine;
using System.ComponentModel.DataAnnotations;
using System.Text;
using BenchmarkDotNet.Attributes;
using Configlue;
using Configlue.Codecs;
using Configlue.Extensibility;
using Configlue.Provider.Json;
using Configlue.Resources;
using Configlue.Source.CommandLine;
using Configlue.Source.Environment;
using Configlue.Sources;
using Configlue.State;
using Configlue.Testing;

[ConfiglueModel("bench-optimization-settings", Version = 1)]
public partial class OptimizationBenchmarkSettings
{
    [Range(0, 100)]
    public int Counter { get; set; } = 3;

    public string Name { get; set; } = "default";

    public bool Enabled { get; set; } = true;
}

[ConfiglueModel("bench-optimization-nested", Version = 1)]
public partial class OptimizationNestedSettings
{
    public string Label { get; set; } = "nested";

    public int Port { get; set; } = 5432;
}

[ConfiglueModel("bench-optimization-root", Version = 1)]
public partial class OptimizationRootSettings
{
    public string Name { get; set; } = "root";

    public OptimizationNestedSettings? Nested { get; set; } = new();
}

[ConfiglueModel("bench-append-collection", Version = 1)]
public partial class AppendCollectionSettings
{
    [ConfiglueMerge(MergeMode.Append)]
    public IReadOnlyList<string> Items { get; set; } = [];
}

[ConfiglueModel("bench-set-union-collection", Version = 1)]
public partial class SetUnionCollectionSettings
{
    [ConfiglueMerge(MergeMode.SetUnion)]
    public IReadOnlyList<string> Items { get; set; } = [];
}

[ConfiglueModel("bench-save-routing", Version = 1)]
public partial class SaveRoutingBenchmarkSettings
{
    public int Counter { get; set; }

    public string Name { get; set; } = string.Empty;
}

[MemoryDiagnoser]
public class ReadValidationBenchmarks
{
    private ConfiglueContext _context = null!;
    private IWritableState<OptimizationBenchmarkSettings> _options = null!;

    [Params(false, true)]
    public bool Validate { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        var store = new InMemoryStateSource<OptimizationBenchmarkSettings.Fragment>(
            new OptimizationBenchmarkSettings.Fragment
            {
                Counter = Optional<int>.Present(10),
                Name = Optional<string>.Present("benchmark"),
                Enabled = Optional<bool>.Present(true),
            }
        );
        var sourceSet = new StateSourceSet<OptimizationBenchmarkSettings.Fragment>([
            new StateSource<OptimizationBenchmarkSettings.Fragment>(
                "benchmark",
                store,
                new StateSourceOptions<OptimizationBenchmarkSettings.Fragment>()
            ),
        ]);
        _context = BenchmarkContextFactory.Create<
            OptimizationBenchmarkSettings,
            OptimizationBenchmarkSettings.Fragment
        >(
            sourceSet,
            model =>
            {
                model.ValidateDataAnnotations = Validate;
                if (Validate)
                {
                    model.AddValidator(new MarkerValidator());
                }
            }
        );
        _options = _context.GetState<OptimizationBenchmarkSettings>();
    }

    [GlobalCleanup]
    public ValueTask CleanupAsync() => _context.DisposeAsync();

    [Benchmark]
    public ValueTask<OptimizationBenchmarkSettings> GetValueAsync() => _options.GetValueAsync();

    private sealed class MarkerValidator : IConfiglueValidator<OptimizationBenchmarkSettings>
    {
        public IReadOnlyList<string> Validate(OptimizationBenchmarkSettings value) =>
            value.Counter < 0 ? ["counter"] : [];
    }
}

[MemoryDiagnoser]
public class LayeredResolutionFallbackBenchmarks
{
    private ConfiglueContext _context = null!;
    private IWritableState<OptimizationBenchmarkSettings> _options = null!;

    [Params(1, 2, 4, 16)]
    public int SourceCount { get; set; }

    [Params("First", "Middle", "Last")]
    public string SuccessPosition { get; set; } = "First";

    [GlobalSetup]
    public void Setup()
    {
        var sources = Enumerable
            .Range(0, SourceCount)
            .Select(index =>
            {
                var successIndex = SuccessPosition switch
                {
                    "First" => 0,
                    "Middle" => SourceCount / 2,
                    "Last" => SourceCount - 1,
                    _ => throw new InvalidOperationException(
                        $"Unknown success position: {SuccessPosition}"
                    ),
                };
                var store =
                    index != successIndex
                        ? new InMemoryStateSource<OptimizationBenchmarkSettings.Fragment>()
                        : new InMemoryStateSource<OptimizationBenchmarkSettings.Fragment>(
                            new OptimizationBenchmarkSettings.Fragment
                            {
                                Counter = Optional<int>.Present(index),
                                Name = Optional<string>.Present($"Layer {index}"),
                                Enabled = Optional<bool>.Present(index % 2 == 0),
                            }
                        );
                return new StateSource<OptimizationBenchmarkSettings.Fragment>(
                    $"layer-{index}",
                    store,
                    new StateSourceOptions<OptimizationBenchmarkSettings.Fragment>
                    {
                        Priority = SourceCount - index,
                        FallbackCondition = StateFallbackCondition.NotFound,
                    }
                );
            })
            .ToArray();
        _context = BenchmarkContextFactory.Create<
            OptimizationBenchmarkSettings,
            OptimizationBenchmarkSettings.Fragment
        >(new StateSourceSet<OptimizationBenchmarkSettings.Fragment>(sources));
        _options = _context.GetState<OptimizationBenchmarkSettings>();
    }

    [GlobalCleanup]
    public ValueTask CleanupAsync() => _context.DisposeAsync();

    [Benchmark]
    public ValueTask<OptimizationBenchmarkSettings> ResolveSourcesAsync() =>
        _options.GetValueAsync();
}

[MemoryDiagnoser]
public class FragmentMergeBenchmarks
{
    private OptimizationBenchmarkSettings.Fragment _base = null!;
    private OptimizationBenchmarkSettings.Fragment _overlay = null!;

    [Params(1, 3)]
    public int PresentMemberCount { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _base = new OptimizationBenchmarkSettings.Fragment { Counter = Optional<int>.Present(1) };
        if (PresentMemberCount == 1)
        {
            _overlay = new OptimizationBenchmarkSettings.Fragment
            {
                Counter = Optional<int>.Present(2),
            };
        }
        else
        {
            _overlay = new OptimizationBenchmarkSettings.Fragment
            {
                Counter = Optional<int>.Present(2),
                Name = Optional<string>.Present("overlay"),
                Enabled = Optional<bool>.Present(false),
            };
        }
    }

    [Benchmark]
    public OptimizationBenchmarkSettings.Fragment Merge() => _base.Merge(_overlay);

    [Benchmark]
    public OptimizationBenchmarkSettings ToModel() => _overlay.ToModel();
}

[MemoryDiagnoser]
public class NestedModelReadBenchmarks
{
    private ConfiglueContext _context = null!;
    private IWritableState<OptimizationRootSettings> _options = null!;

    [GlobalSetup]
    public void Setup()
    {
        var store = new InMemoryStateSource<OptimizationRootSettings.Fragment>(
            new OptimizationRootSettings.Fragment
            {
                Name = Optional<string>.Present("root"),
                Nested = Optional<OptimizationNestedSettings.Fragment?>.Present(
                    new OptimizationNestedSettings.Fragment
                    {
                        Label = Optional<string>.Present("nested"),
                        Port = Optional<int>.Present(6432),
                    }
                ),
            }
        );
        _context = BenchmarkContextFactory.Create<
            OptimizationRootSettings,
            OptimizationRootSettings.Fragment
        >(
            new StateSourceSet<OptimizationRootSettings.Fragment>([
                new StateSource<OptimizationRootSettings.Fragment>(
                    "nested",
                    store,
                    new StateSourceOptions<OptimizationRootSettings.Fragment>()
                ),
            ])
        );
        _options = _context.GetState<OptimizationRootSettings>();
    }

    [GlobalCleanup]
    public ValueTask CleanupAsync() => _context.DisposeAsync();

    [Benchmark]
    public ValueTask<OptimizationRootSettings> GetValueAsync() => _options.GetValueAsync();
}

[MemoryDiagnoser]
public class CollectionMergeBenchmarks
{
    private ConfiglueContext _appendContext = null!;
    private ConfiglueContext _setUnionContext = null!;
    private IWritableState<AppendCollectionSettings> _append = null!;
    private IWritableState<SetUnionCollectionSettings> _setUnion = null!;

    [Params(2, 8)]
    public int SourceCount { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _appendContext = BenchmarkContextFactory.Create<
            AppendCollectionSettings,
            AppendCollectionSettings.Fragment
        >(new StateSourceSet<AppendCollectionSettings.Fragment>(CreateAppendSources()));
        _append = _appendContext.GetState<AppendCollectionSettings>();
        _setUnionContext = BenchmarkContextFactory.Create<
            SetUnionCollectionSettings,
            SetUnionCollectionSettings.Fragment
        >(new StateSourceSet<SetUnionCollectionSettings.Fragment>(CreateSetUnionSources()));
        _setUnion = _setUnionContext.GetState<SetUnionCollectionSettings>();
    }

    [GlobalCleanup]
    public async ValueTask CleanupAsync()
    {
        await _appendContext.DisposeAsync().ConfigureAwait(false);
        await _setUnionContext.DisposeAsync().ConfigureAwait(false);
    }

    [Benchmark]
    public ValueTask<AppendCollectionSettings> AppendResolveAsync() => _append.GetValueAsync();

    [Benchmark]
    public ValueTask<SetUnionCollectionSettings> SetUnionResolveAsync() =>
        _setUnion.GetValueAsync();

    private StateSource<AppendCollectionSettings.Fragment>[] CreateAppendSources() =>
        Enumerable
            .Range(0, SourceCount)
            .Select(index =>
            {
                var store = new InMemoryStateSource<AppendCollectionSettings.Fragment>(
                    new AppendCollectionSettings.Fragment
                    {
                        Items = Optional<IReadOnlyList<string>>.Present([
                            $"item-{index}",
                            "shared",
                        ]),
                    }
                );
                return new StateSource<AppendCollectionSettings.Fragment>(
                    $"append-{index}",
                    store,
                    new StateSourceOptions<AppendCollectionSettings.Fragment>
                    {
                        Priority = SourceCount - index,
                    }
                );
            })
            .ToArray();

    private StateSource<SetUnionCollectionSettings.Fragment>[] CreateSetUnionSources() =>
        Enumerable
            .Range(0, SourceCount)
            .Select(index =>
            {
                var store = new InMemoryStateSource<SetUnionCollectionSettings.Fragment>(
                    new SetUnionCollectionSettings.Fragment
                    {
                        Items = Optional<IReadOnlyList<string>>.Present([
                            $"item-{index}",
                            "shared",
                        ]),
                    }
                );
                return new StateSource<SetUnionCollectionSettings.Fragment>(
                    $"union-{index}",
                    store,
                    new StateSourceOptions<SetUnionCollectionSettings.Fragment>
                    {
                        Priority = SourceCount - index,
                    }
                );
            })
            .ToArray();
}

[MemoryDiagnoser]
public class SaveRoutingBenchmarks
{
    private ConfiglueContext _singleContext = null!;
    private ConfiglueContext _multiContext = null!;
    private IWritableState<SaveRoutingBenchmarkSettings> _single = null!;
    private IConfiglueEditSessions<SaveRoutingBenchmarkSettings> _multi = null!;
    private StateWritePlan _writePlan = null!;
    private int _counter;

    [GlobalSetup]
    public void Setup()
    {
        var singleStore = new InMemoryStateSource<SaveRoutingBenchmarkSettings.Fragment>(
            new SaveRoutingBenchmarkSettings.Fragment
            {
                Counter = Optional<int>.Present(0),
                Name = Optional<string>.Present("single"),
            }
        );
        _writePlan = new StateWritePlan(
            new Dictionary<string, string>(StringComparer.Ordinal) { ["Name"] = "right" }
        );
        _singleContext = BenchmarkContextFactory.Create<
            SaveRoutingBenchmarkSettings,
            SaveRoutingBenchmarkSettings.Fragment
        >(
            new StateSourceSet<SaveRoutingBenchmarkSettings.Fragment>([
                new StateSource<SaveRoutingBenchmarkSettings.Fragment>(
                    "single",
                    singleStore,
                    new StateSourceOptions<SaveRoutingBenchmarkSettings.Fragment>
                    {
                        Writer = singleStore,
                    }
                ),
            ])
        );
        _single = _singleContext.GetState<SaveRoutingBenchmarkSettings>();

        var left = new InMemoryStateSource<SaveRoutingBenchmarkSettings.Fragment>(
            new SaveRoutingBenchmarkSettings.Fragment
            {
                Counter = Optional<int>.Present(0),
                Name = Optional<string>.Present("left"),
            }
        );
        var right = new InMemoryStateSource<SaveRoutingBenchmarkSettings.Fragment>(
            new SaveRoutingBenchmarkSettings.Fragment { Name = Optional<string>.Present("right") }
        );
        _multiContext = BenchmarkContextFactory.Create<
            SaveRoutingBenchmarkSettings,
            SaveRoutingBenchmarkSettings.Fragment
        >(
            new StateSourceSet<SaveRoutingBenchmarkSettings.Fragment>([
                new StateSource<SaveRoutingBenchmarkSettings.Fragment>(
                    "left",
                    left,
                    new StateSourceOptions<SaveRoutingBenchmarkSettings.Fragment>
                    {
                        Priority = 100,
                        Writer = left,
                    }
                ),
                new StateSource<SaveRoutingBenchmarkSettings.Fragment>(
                    "right",
                    right,
                    new StateSourceOptions<SaveRoutingBenchmarkSettings.Fragment>
                    {
                        Priority = 50,
                        Writer = right,
                    }
                ),
            ])
        );
        _multi = _multiContext.GetEditSessions<SaveRoutingBenchmarkSettings>();
    }

    [GlobalCleanup]
    public async ValueTask CleanupAsync()
    {
        await _singleContext.DisposeAsync().ConfigureAwait(false);
        await _multiContext.DisposeAsync().ConfigureAwait(false);
    }

    [Benchmark]
    public async Task SaveSingleSourceAsync()
    {
        var next = Interlocked.Increment(ref _counter);
        await _single.SaveAsync(patch => patch.Counter = 1 + (next % 90)).ConfigureAwait(false);
    }

    [Benchmark]
    public async Task SaveMultiSourceRoutedAsync()
    {
        var next = Interlocked.Increment(ref _counter);
        using var session = await _multi.OpenEditSessionAsync(_writePlan).ConfigureAwait(false);
        session.Value.Counter = 1 + (next % 90);
        session.Value.Name = $"name-{next}";
        await session.CommitAsync().ConfigureAwait(false);
    }
}

[MemoryDiagnoser]
public class JsonCodecLayoutBenchmarks
{
    private JsonStateCodec<OptimizationBenchmarkSettings.Fragment> _codec = null!;
    private JsonStateCodec<OptimizationBenchmarkSettings> _plainCodec = null!;
    private StateCodecContext _context;
    private StateCodecContext _plainContext;
    private OptimizationBenchmarkSettings.Fragment _fragment = null!;
    private OptimizationBenchmarkSettings _plainValue = null!;
    private ArrayBufferWriter<byte> _buffer = null!;
    private ArrayBufferWriter<byte> _plainBuffer = null!;
    private ReadOnlySequence<byte> _sequence;

    [Params(DocumentLayout.Simple, DocumentLayout.Detailed)]
    public DocumentLayout Layout { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _codec = new JsonStateCodec<OptimizationBenchmarkSettings.Fragment>(
            documentLayout: new DocumentLayoutOptions { Layout = Layout }
        );
        _plainCodec = new JsonStateCodec<OptimizationBenchmarkSettings>(
            documentLayout: new DocumentLayoutOptions { Layout = Layout }
        );
        _context = new StateCodecContext(new StateSchemaMetadata("bench-optimization-settings", 1));
        _plainContext = default;
        _fragment = new OptimizationBenchmarkSettings.Fragment
        {
            Counter = Optional<int>.Present(10),
            Name = Optional<string>.Present("benchmark"),
            Enabled = Optional<bool>.Present(true),
        };
        _plainValue = new OptimizationBenchmarkSettings
        {
            Counter = 10,
            Name = "benchmark",
            Enabled = true,
        };
        _buffer = new ArrayBufferWriter<byte>();
        _plainBuffer = new ArrayBufferWriter<byte>();
        _codec.Serialize(_fragment, _buffer, in _context);
        _plainCodec.Serialize(_plainValue, _plainBuffer, in _plainContext);
        _sequence = new ReadOnlySequence<byte>(_buffer.WrittenMemory.ToArray());
    }

    [Benchmark]
    public void Serialize()
    {
        _buffer.Clear();
        _codec.Serialize(_fragment, _buffer, in _context);
    }

    [Benchmark]
    public void SerializeWithoutSchema()
    {
        _plainBuffer.Clear();
        _plainCodec.Serialize(_plainValue, _plainBuffer, in _plainContext);
    }

    [Benchmark]
    public OptimizationBenchmarkSettings.Fragment? Deserialize() =>
        _codec.Deserialize(in _sequence, in _context);
}

[MemoryDiagnoser]
public class StateRevisionVectorBenchmarks
{
    private StateRevision[] _revisions = [];
    private SourceId _lookupId;

    [Params(0, 1, 2, 4, 16)]
    public int SourceCount { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _revisions = new StateRevision[SourceCount];
        for (var index = 0; index < _revisions.Length; index++)
        {
            var sourceId = SourceId.From($"source-{index}");
            _revisions[index] = new StateRevision(sourceId, $"revision-{index}");
            _lookupId = sourceId;
        }
    }

    [Benchmark]
    public bool ConstructAndLookup()
    {
        var vector = StateRevisionVector.FromSpan(_revisions);
        return vector.TryGetRevision(_lookupId, out _);
    }
}

[MemoryDiagnoser]
public class JsonSectionBenchmarks
{
    private string _directory = null!;
    private FileResource _file = null!;
    private SerializedStateReader<OptimizationBenchmarkSettings.Fragment> _reader = null!;
    private ConfiglueContext _context = null!;
    private IWritableState<OptimizationBenchmarkSettings> _options = null!;
    private int _counter;

    [GlobalSetup]
    public async Task SetupAsync()
    {
        _directory = Path.Combine(
            Path.GetTempPath(),
            $"configlue-section-bench-{Guid.NewGuid():N}"
        );
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "appsettings.json");
        await File.WriteAllTextAsync(
                path,
                """
                {
                  "App": {
                    "Settings": { "$version": 1, "Counter": 5 },
                    "Other": { "Value": "keep-nested" }
                  },
                  "OtherSection": { "Value": "keep-root" }
                }
                """
            )
            .ConfigureAwait(false);
        _file = new FileResource(path, new FileResourceOptions { CreateBackup = false });
        var section = new JsonSectionResource(_file, "App:Settings");
        var codec = new JsonStateCodec<OptimizationBenchmarkSettings.Fragment>();
        _reader = new SerializedStateReader<OptimizationBenchmarkSettings.Fragment>(section, codec);
        var writer = new SerializedStateWriter<OptimizationBenchmarkSettings.Fragment>(
            section,
            codec
        );
        _context = BenchmarkContextFactory.Create<
            OptimizationBenchmarkSettings,
            OptimizationBenchmarkSettings.Fragment
        >(
            new StateSourceSet<OptimizationBenchmarkSettings.Fragment>([
                new StateSource<OptimizationBenchmarkSettings.Fragment>(
                    "section",
                    _reader,
                    new StateSourceOptions<OptimizationBenchmarkSettings.Fragment>
                    {
                        Writer = writer,
                        Watcher = section,
                    }
                ),
            ])
        );
        _options = _context.GetState<OptimizationBenchmarkSettings>();
    }

    [GlobalCleanup]
    public async Task CleanupAsync()
    {
        await _context.DisposeAsync().ConfigureAwait(false);
        _file.Dispose();
        Directory.Delete(_directory, recursive: true);
    }

    [Benchmark]
    public ValueTask<StateReadResult<OptimizationBenchmarkSettings.Fragment>> ReadAsync() =>
        _reader.ReadAsync(ConfiglueResourceContext.Default);

    [Benchmark]
    public async Task SaveAsync()
    {
        var next = Interlocked.Increment(ref _counter);
        await _options.SaveAsync(patch => patch.Counter = 1 + (next % 90)).ConfigureAwait(false);
    }
}

[MemoryDiagnoser]
public class EnvironmentSourceBenchmarks
{
    private StateSource<OptimizationBenchmarkSettings.Fragment> _source = null!;

    [GlobalSetup]
    public void Setup()
    {
        var variables = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["APP__COUNTER"] = "7",
            ["APP__NAME"] = "environment",
            ["APP__ENABLED"] = "true",
        };
        _source = EnvironmentStateSource.FromEnvironment<
            OptimizationBenchmarkSettings,
            OptimizationBenchmarkSettings.Fragment
        >("environment", "APP", environmentVariables: () => variables);
    }

    [Benchmark]
    public ValueTask<StateReadResult<OptimizationBenchmarkSettings.Fragment>> ReadAsync() =>
        _source.Reader.ReadAsync(ConfiglueResourceContext.Default);
}

[MemoryDiagnoser]
public class CommandLineSourceBenchmarks
{
    private ConfiglueContext _context = null!;
    private IWritableState<OptimizationBenchmarkSettings> _options = null!;

    [GlobalSetup]
    public void Setup()
    {
        var counterOption = new Option<int>("--counter");
        var root = new RootCommand();
        root.Options.Add(counterOption);
        var parseResult = root.Parse(["--counter", "7"]);

        _context = ConfiglueApp.CreateContext(builder =>
        {
            builder.Add<OptimizationBenchmarkSettings>(model =>
                model.Sources(sources =>
                    sources.FromCommandLine(
                        new CommandLineSourceOptions
                        {
                            Id = "command-line",
                            ParseResult = parseResult,
                        },
                        mappings => mappings.Map(counterOption, "Counter")
                    )
                )
            );
        });
        _options = _context.GetState<OptimizationBenchmarkSettings>();
    }

    [GlobalCleanup]
    public void Cleanup() => _context.Dispose();

    [Benchmark]
    public ValueTask<OptimizationBenchmarkSettings> ReadAsync() => _options.GetValueAsync();
}

[MemoryDiagnoser]
public class FileBackupBenchmarks
{
    private string _directory = null!;
    private FileResource _resource = null!;
    private ConfiglueContext _context = null!;
    private IWritableState<OptimizationBenchmarkSettings> _options = null!;
    private int _counter;

    [Params(1, 3, 10)]
    public int BackupMaxCount { get; set; }

    [GlobalSetup]
    public async Task SetupAsync()
    {
        _directory = Path.Combine(Path.GetTempPath(), $"configlue-backup-bench-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "settings.json");
        _resource = new FileResource(
            path,
            new FileResourceOptions
            {
                CreateBackup = true,
                BackupMaxCount = BackupMaxCount,
                BackupDirectory = "/",
                LockDirectory = "/",
            }
        );
        var source = SerializedStateSource.FromResource<OptimizationBenchmarkSettings.Fragment>(
            "backup",
            _resource,
            new JsonStateCodec<OptimizationBenchmarkSettings.Fragment>(),
            physicalOrigin: path
        );
        _context = BenchmarkContextFactory.Create<
            OptimizationBenchmarkSettings,
            OptimizationBenchmarkSettings.Fragment
        >(new StateSourceSet<OptimizationBenchmarkSettings.Fragment>([source]));
        _options = _context.GetState<OptimizationBenchmarkSettings>();
        await _resource
            .WriteAsync(
                ConfiglueResourceContext.Default,
                new ResourceWriteRequest(Encoding.UTF8.GetBytes("""{"$version":1,"Counter":0}"""))
            )
            .ConfigureAwait(false);
    }

    [GlobalCleanup]
    public async Task CleanupAsync()
    {
        await _context.DisposeAsync().ConfigureAwait(false);
        _resource.Dispose();
        Directory.Delete(_directory, recursive: true);
    }

    [Benchmark]
    public async Task SaveAsync()
    {
        var next = Interlocked.Increment(ref _counter);
        await _options.SaveAsync(patch => patch.Counter = 1 + (next % 90)).ConfigureAwait(false);
    }
}
