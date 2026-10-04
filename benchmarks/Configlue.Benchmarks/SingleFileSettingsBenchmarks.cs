using System.Text.Json;
using BenchmarkDotNet.Attributes;
using Configlue;
using Configlue.Provider.Json;
using Microsoft.Extensions.Configuration;

[ConfiglueModel("configlue-single-file-settings", Version = 1)]
public partial class SingleFileBenchmarkSettings
{
    public string Name { get; set; } = "World";

    public string Theme { get; set; } = "System";

    public int Counter { get; set; }
}

/// <summary>Dedicated benchmark group for the single-file settings scenario (issue #231).</summary>
/// <remarks>
/// <para>
/// Compares the documented zero-ceremony path
/// (<c>config.Add&lt;T&gt;().UseLocalJson(path)</c> plus <c>GetValueAsync</c>/<c>SaveAsync</c>)
/// against baselines with no framework: direct <c>System.Text.Json</c> plus <c>File</c> I/O,
/// and Microsoft configuration binding where a read comparison is meaningful.
/// </para>
/// <para>
/// The objective is not to beat direct serialization. The objective is to quantify and cap
/// the framework overhead Configlue adds in exchange for safe write/update semantics
/// (atomic replace, backup, cross-process lock, conflict detection, watching) and the
/// ability to grow into layered configuration without rewriting consumers.
/// </para>
/// <para>Overhead budget (to be confirmed with published runs on the benchmark machine):</para>
/// <list type="bullet">
/// <item>Warm read: at most 3x the direct STJ deserialize baseline (same payload, same file).</item>
/// <item>Small patch save: at most 3x the direct STJ serialize-plus-write baseline, including
/// the default backup generation. Disabling backups must close most of the gap.</item>
/// <item>Cold initialization plus first read: bounded by one file read plus one-time model
/// setup; no per-source fan-out structures may scale with unrelated capacity.</item>
/// <item>Watched reload: dominated by filesystem notification plus the configured debounce,
/// not by resolver work.</item>
/// </list>
/// </remarks>
[MemoryDiagnoser]
public class SingleFileSettingsBenchmarks
{
    private string _directory = null!;
    private string _configluePath = null!;
    private string _baselinePath = null!;
    private string _msPath = null!;
    private ConfiglueContext _context = null!;
    private IWritableState<SingleFileBenchmarkSettings> _state = null!;
    private IConfigurationRoot _msConfiguration = null!;
    private IDisposable? _watchSubscription;
    private int _counter;

    private static readonly JsonSerializerOptions BaselineJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    [GlobalSetup]
    public async Task SetupAsync()
    {
        _directory = Path.Combine(Path.GetTempPath(), $"configlue-single-file-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_directory);
        _configluePath = Path.Combine(_directory, "settings.json");
        _baselinePath = Path.Combine(_directory, "baseline.json");
        _msPath = Path.Combine(_directory, "ms.json");

        _context = ConfiglueApp.CreateContext(builder =>
        {
            builder.Add<SingleFileBenchmarkSettings>().UseLocalJson(_configluePath);
        });
        _state = _context.GetState<SingleFileBenchmarkSettings>();
        _ = await _state.GetValueAsync().ConfigureAwait(false);
        await _state.SaveAsync(patch => patch.Counter = 1).ConfigureAwait(false);

        var baselineDto = new SingleFileDto { Name = "World", Theme = "System", Counter = 1 };
        await File.WriteAllTextAsync(
                _baselinePath,
                JsonSerializer.Serialize(baselineDto, BaselineJsonOptions)
            )
            .ConfigureAwait(false);
        await File.WriteAllTextAsync(
                _msPath,
                JsonSerializer.Serialize(baselineDto, BaselineJsonOptions)
            )
            .ConfigureAwait(false);
        _msConfiguration = new ConfigurationBuilder()
            .AddJsonFile(_msPath, optional: false, reloadOnChange: false)
            .Build();

        // Start the watcher once so WatchedReload measures notification, not startup.
        _watchSubscription = _state.OnChange(_ => { });
        _ = await _state.GetValueAsync().ConfigureAwait(false);
    }

    [GlobalCleanup]
    public async Task CleanupAsync()
    {
        _watchSubscription?.Dispose();
        await _context.DisposeAsync().ConfigureAwait(false);
        (_msConfiguration as IDisposable)?.Dispose();
        Directory.Delete(_directory, recursive: true);
    }

    [Benchmark(Baseline = true)]
    public async Task<SingleFileDto?> StjDirectReadAsync()
    {
        var json = await File.ReadAllTextAsync(_baselinePath).ConfigureAwait(false);
        return JsonSerializer.Deserialize<SingleFileDto>(json, BaselineJsonOptions);
    }

    [Benchmark]
    public SingleFileDto? MsConfigurationRead() =>
        _msConfiguration.Get<SingleFileDto>();

    [Benchmark]
    public ValueTask<SingleFileBenchmarkSettings> ConfiglueWarmReadAsync() =>
        _state.GetValueAsync();

    [Benchmark]
    public async Task<SingleFileBenchmarkSettings> ConfiglueColdInitAndFirstReadAsync()
    {
        await using var context = ConfiglueApp.CreateContext(builder =>
        {
            builder.Add<SingleFileBenchmarkSettings>().UseLocalJson(_configluePath);
        });
        return await context
            .GetState<SingleFileBenchmarkSettings>()
            .GetValueAsync()
            .ConfigureAwait(false);
    }

    [Benchmark]
    public async Task ConfigluePatchSaveAsync()
    {
        var next = Interlocked.Increment(ref _counter);
        await _state.SaveAsync(patch => patch.Counter = next).ConfigureAwait(false);
    }

    [Benchmark]
    public async Task StjDirectWriteAsync()
    {
        var next = Interlocked.Increment(ref _counter);
        var payload = JsonSerializer.Serialize(
            new SingleFileDto { Name = "World", Theme = "System", Counter = next },
            BaselineJsonOptions
        );
        await File.WriteAllTextAsync(_baselinePath, payload).ConfigureAwait(false);
    }

    [Benchmark]
    public async Task ConfiglueWatchedReloadAsync()
    {
        // Read the current value first: each benchmark process runs its own setup, so a
        // shared incrementing counter could rewrite the value that is already stored and
        // produce no change notification at all.
        var baseline = await _state.GetValueAsync().ConfigureAwait(false);
        var reloaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var subscription = _state.OnChange(changed =>
        {
            if (changed.Counter == baseline.Counter + 1000)
            {
                reloaded.TrySetResult();
            }
        });
        var node = new System.Text.Json.Nodes.JsonObject
        {
            ["$version"] = 1,
            ["Name"] = "World",
            ["Theme"] = "System",
            ["Counter"] = baseline.Counter + 1000,
        };
        var payload = node.ToJsonString();
        var written = false;
        for (var attempt = 0; !written && attempt < 100; attempt++)
        {
            try
            {
                await File.WriteAllTextAsync(_configluePath, payload).ConfigureAwait(false);
                written = true;
            }
            catch (IOException) when (attempt < 99)
            {
                // The runtime's watcher-triggered re-read may hold the file briefly.
                await Task.Delay(50).ConfigureAwait(false);
            }
        }
        await reloaded.Task.WaitAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(false);
    }

    public sealed class SingleFileDto
    {
        public string Name { get; set; } = string.Empty;

        public string Theme { get; set; } = string.Empty;

        public int Counter { get; set; }
    }
}
